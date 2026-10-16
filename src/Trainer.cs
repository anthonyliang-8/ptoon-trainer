using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// pla_toon offline/bot-match trainer: god mode, infinite ammo, bot-only head aim (hold right mouse).
class Trainer
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    const int VK_RBUTTON = 0x02;
    const int COMPONENT_TO_WORLD = 0x180; // USceneComponent::ComponentToWorld (not reflected; found by probing)
    const int VSLOT_TAKEDAMAGE = 0x468;   // AC_Soldier_Health_C::TakeDamage virtual slot (from execTakeDamage thunk)
    const int VTABLE_COPY_SIZE = 0x1000;

    static Mem m; static Ue ue;
    static long clsPC, clsSoldier, clsWeapon;
    // Reflected offsets
    static int pcPlayer, pcSoldier, pcCamMgr, camPov, povLoc, povRot;
    static int sHealthComp, sWeapComp, sYawComp, sPitchComp, sHeadHitbox, sIsBotOff, sIsDeadOff;
    static byte sIsBotMask, sIsDeadMask;
    static int hpHealth, wcSelected, wClip, wClipSize, relRot;

    static volatile bool god, ammo, aim;
    static volatile float fov = 25f, headZ = 4f;
    static volatile string status = "";
    static long[] soldiers = new long[0];
    static long localPC;
    static long godVtable, godStub, origVtable, patchedComp;

    static void Main()
    {
        Console.WriteLine("Attaching to pla_toon...");
        m = new Mem("pla_toon-Win64-Shipping");
        ue = new Ue(m);
        ResolveOffsets();
        new Thread(RefreshLoop) { IsBackground = true }.Start();
        new Thread(TickLoop) { IsBackground = true }.Start();

        while (true)
        {
            DrawMenu();
            ConsoleKey k = Console.ReadKey(true).Key;
            if (k == ConsoleKey.D1) god = !god;
            else if (k == ConsoleKey.D2) ammo = !ammo;
            else if (k == ConsoleKey.D3) aim = !aim;
            else if (k == ConsoleKey.OemPlus || k == ConsoleKey.Add) fov = Math.Min(180, fov + 5);
            else if (k == ConsoleKey.OemMinus || k == ConsoleKey.Subtract) fov = Math.Max(5, fov - 5);
            else if (k == ConsoleKey.Q) { god = false; Thread.Sleep(50); RestoreGod(); return; }
        }
    }

    static void DrawMenu()
    {
        Console.Clear();
        Console.WriteLine("pla_toon trainer (offline / bot matches)");
        Console.WriteLine("  [1] God mode       : {0}", god ? "ON" : "off");
        Console.WriteLine("  [2] Infinite ammo  : {0}", ammo ? "ON" : "off");
        Console.WriteLine("  [3] Bot head aim   : {0}  (hold right mouse)  FOV {1}deg  [+/-]", aim ? "ON" : "off", fov);
        Console.WriteLine("  [Q] Quit (restores god-mode patch)");
        Console.WriteLine();
        Console.WriteLine(status);
    }

    static void ResolveOffsets()
    {
        clsPC = ue.FindClass("BP_platoonPlayerController_C");
        clsSoldier = ue.FindClass("BP_Sold2_Pawn_C");
        clsWeapon = ue.FindClass("BP_Weapon_C");
        long clsHealth = ue.FindClass("AC_Soldier_Health_C"), clsWeaps = ue.FindClass("AC_Soldier_Weapons_C");
        long clsCam = ue.FindClass("PlayerCameraManager"), clsScene = ue.FindClass("SceneComponent");

        pcPlayer = ue.Off(clsPC, "Player");
        pcSoldier = ue.Off(clsPC, "plo_SoldierRef");
        pcCamMgr = ue.Off(clsPC, "PlayerCameraManager");
        long cacheProp = ue.FindProp(clsCam, "CameraCachePrivate");
        long povProp = ue.FindProp(m.Ptr(cacheProp + Ue.PROP_INNER), "POV");
        long viewInfo = m.Ptr(povProp + Ue.PROP_INNER);
        camPov = ue.PropOffset(cacheProp) + ue.PropOffset(povProp);
        povLoc = ue.Off(viewInfo, "Location");
        povRot = ue.Off(viewInfo, "Rotation");

        sHealthComp = ue.Off(clsSoldier, "AC_Soldier_Health");
        sWeapComp = ue.Off(clsSoldier, "AC_Soldier_Weapons");
        sYawComp = ue.Off(clsSoldier, "V3_ZRot_Trans");
        sPitchComp = ue.Off(clsSoldier, "V3_LookUpDown_Trans");
        sHeadHitbox = ue.Off(clsSoldier, "playerHitboxes_hb_head");
        long p = ue.FindProp(clsSoldier, "isBot");
        sIsBotOff = ue.PropOffset(p) + m.U8(p + Ue.BOOL_BYTEOFFSET); sIsBotMask = m.U8(p + Ue.BOOL_BYTEMASK);
        p = ue.FindProp(clsSoldier, "isDead");
        sIsDeadOff = ue.PropOffset(p) + m.U8(p + Ue.BOOL_BYTEOFFSET); sIsDeadMask = m.U8(p + Ue.BOOL_BYTEMASK);

        hpHealth = ue.Off(clsHealth, "Health");
        wcSelected = ue.Off(clsWeaps, "SelectedWeap_R_Ref");
        wClip = ue.Off(clsWeapon, "AmmoInClip");
        wClipSize = ue.Off(clsWeapon, "ClipSize");
        relRot = ue.Off(clsScene, "RelativeRotation");
    }

    // Rescans GObjects for the local controller and all soldiers (slow, so off the tick thread).
    static void RefreshLoop()
    {
        while (true)
        {
            try
            {
                List<long> found = new List<long>();
                long pc = 0;
                foreach (long o in ue.AllObjects())
                {
                    long c = ue.ClassOf(o);
                    if (c == clsSoldier) found.Add(o);
                    else if (c == clsPC && m.Ptr(o + pcPlayer) != 0) pc = o;
                }
                soldiers = found.ToArray();
                localPC = pc;
            }
            catch (Exception e) { status = "refresh error: " + e.Message; }
            Thread.Sleep(2000);
        }
    }

    static void TickLoop()
    {
        int n = 0;
        while (true)
        {
            try
            {
                long me = localPC == 0 ? 0 : m.Ptr(localPC + pcSoldier);
                if (me != 0)
                {
                    long hp = m.Ptr(me + sHealthComp);
                    if (god) ApplyGod(hp); else RestoreGod();
                    if (god && m.I32(hp + hpHealth) < 100) m.WriteI32(hp + hpHealth, 100);
                    if (ammo) RefillClip(me);
                    string target = "";
                    if (aim && (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0) target = Aim(me);
                    if (++n % 50 == 0)
                        status = string.Format("soldiers={0}  health={1}  {2}", soldiers.Length, m.I32(hp + hpHealth), target);
                }
                else if (++n % 50 == 0) status = "Waiting for local soldier (join/start a match)...";
            }
            catch (Exception e) { status = "tick error: " + e.Message; }
            Thread.Sleep(4);
        }
    }

    // Points only our health component at a vtable copy whose TakeDamage slot is a bare `ret`.
    static void ApplyGod(long hp)
    {
        if (godVtable == 0)
        {
            godVtable = m.Alloc(VTABLE_COPY_SIZE + 16);
            godStub = godVtable + VTABLE_COPY_SIZE;
            m.Write(godStub, new byte[] { 0xC3 });
        }
        if (hp == patchedComp && m.Ptr(hp) == godVtable) return;
        RestoreGod(); // previous component (e.g. before respawn)
        origVtable = m.Ptr(hp);
        m.Write(godVtable, m.Bytes(origVtable, VTABLE_COPY_SIZE));
        m.WritePtr(godVtable + VSLOT_TAKEDAMAGE, godStub);
        m.WritePtr(hp, godVtable);
        patchedComp = hp;
    }

    static void RestoreGod()
    {
        if (patchedComp != 0 && m.Ptr(patchedComp) == godVtable) m.WritePtr(patchedComp, origVtable);
        patchedComp = 0;
    }

    static void RefillClip(long me)
    {
        long weap = m.Ptr(m.Ptr(me + sWeapComp) + wcSelected);
        if (weap == 0 || !ue.IsA(weap, "BP_Weapon_C")) return;
        int size = m.I32(weap + wClipSize);
        if (m.I32(weap + wClip) < size) m.WriteI32(weap + wClip, size);
    }

    static float[] Vec(long a) { return new float[] { m.F32(a), m.F32(a + 4), m.F32(a + 8) }; }

    static float Norm(float a) { a %= 360f; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }

    // Turns the view toward the bot head hitbox closest to the crosshair within the FOV cone.
    static string Aim(long me)
    {
        long cam = m.Ptr(localPC + pcCamMgr);
        float[] eye = Vec(cam + camPov + povLoc);
        float[] view = Vec(cam + camPov + povRot); // pitch, yaw, roll
        long best = 0; float bestDist = fov, bestYaw = 0, bestPitch = 0;
        foreach (long s in soldiers)
        {
            if (s == me || (m.U8(s + sIsBotOff) & sIsBotMask) == 0 || (m.U8(s + sIsDeadOff) & sIsDeadMask) != 0) continue;
            long hp = m.Ptr(s + sHealthComp);
            if (hp == 0 || m.I32(hp + hpHealth) <= 0) continue;
            long head = m.Ptr(s + sHeadHitbox);
            if (head == 0) continue;
            float[] h = Vec(head + COMPONENT_TO_WORLD + 0x10);
            double dx = h[0] - eye[0], dy = h[1] - eye[1], dz = h[2] + headZ - eye[2];
            float yaw = (float)(Math.Atan2(dy, dx) * 180 / Math.PI);
            float pitch = (float)(Math.Atan2(dz, Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI);
            float dYaw = Norm(yaw - view[1]), dPitch = pitch - view[0];
            float d = (float)Math.Sqrt(dYaw * dYaw + dPitch * dPitch);
            if (d < bestDist) { best = s; bestDist = d; bestYaw = yaw; bestPitch = pitch; }
        }
        if (best == 0) return "";
        long yawComp = m.Ptr(me + sYawComp), pitchComp = m.Ptr(me + sPitchComp);
        float curYaw = m.F32(yawComp + relRot + 4);
        m.WriteF32(yawComp + relRot + 4, curYaw + Norm(bestYaw - curYaw));
        m.WriteF32(pitchComp + relRot, bestPitch);
        return "target: " + ue.ObjName(best);
    }
}
