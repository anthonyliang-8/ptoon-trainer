using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// pla_toon offline/bot-match trainer: god mode, infinite ammo, bot-only head aim (hold right mouse), no spread, bot ESP.
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
    static int sTeam, sPloOn, povFov;
    static int[] sHitboxes;

    // No spread: weapon fields (BP_Weapon_C) forced while held, and the recoil-level fields on AC_Soldier_Weapons_C.
    // recoil_MasterMulti is left alone because AC_HackerFucker_C checks it.
    static readonly string[] SPREAD_WEAP_FIELDS = { "Accuracy", "shotgunSpread", "movementRecoilMulti", "Recoil_HeatUpRate", "recoil_MaxLevel" };
    static readonly float[] SPREAD_WEAP_VALUES = { 1f, 0f, 0f, 0f, 0f };
    static readonly string[] SPREAD_COMP_LEVELS = { "Recoil_Level_R_FromWeap", "Recoil_Level_FromMovement", "Recoil_Level_R_Total" };
    static int[] wSpread, cLevels;
    static int cHeatUp, cMaxLevel;
    static long spreadWeap, spreadComp;
    static float[] spreadOrig;

    static readonly string[] HITBOXES = { "upperarm_l", "thigh_r", "thigh_l", "lowerarm_r", "lowerarm_l", "head", "hand_r",
        "upperarm_r", "foot_r", "foot_l", "calf_r", "calf_l", "bodyUpper", "bodyLower", "hand_l" };

    static volatile bool god, ammo, aim, noSpread, esp;
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
        Thread ui = new Thread(delegate() { System.Windows.Forms.Application.Run(new Overlay(m.Pid, EspTargets, delegate { return esp; })); });
        ui.IsBackground = true; ui.SetApartmentState(ApartmentState.STA); ui.Start();

        while (true)
        {
            DrawMenu();
            ConsoleKey k = Console.ReadKey(true).Key;
            if (k == ConsoleKey.D1) god = !god;
            else if (k == ConsoleKey.D2) ammo = !ammo;
            else if (k == ConsoleKey.D3) aim = !aim;
            else if (k == ConsoleKey.D4) noSpread = !noSpread;
            else if (k == ConsoleKey.D5) esp = !esp;
            else if (k == ConsoleKey.OemPlus || k == ConsoleKey.Add) fov = Math.Min(180, fov + 5);
            else if (k == ConsoleKey.OemMinus || k == ConsoleKey.Subtract) fov = Math.Max(5, fov - 5);
            else if (k == ConsoleKey.Q) { god = false; noSpread = false; Thread.Sleep(50); RestoreGod(); RestoreSpread(); return; }
        }
    }

    static void DrawMenu()
    {
        Console.Clear();
        Console.WriteLine("pla_toon trainer (offline / bot matches)");
        Console.WriteLine("  [1] God mode       : {0}", god ? "ON" : "off");
        Console.WriteLine("  [2] Infinite ammo  : {0}", ammo ? "ON" : "off");
        Console.WriteLine("  [3] Bot head aim   : {0}  (hold right mouse)  FOV {1}deg  [+/-]", aim ? "ON" : "off", fov);
        Console.WriteLine("  [4] No spread      : {0}", noSpread ? "ON" : "off");
        Console.WriteLine("  [5] Bot ESP        : {0}  (windowed/borderless only)", esp ? "ON" : "off");
        Console.WriteLine("  [Q] Quit (restores god-mode and weapon patches)");
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
        povFov = ue.Off(viewInfo, "FOV");

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

        sTeam = ue.Off(clsSoldier, "Team");
        p = ue.FindProp(clsSoldier, "plo_On");
        sPloOn = ue.PropOffset(p) + m.U8(p + Ue.BOOL_BYTEOFFSET); // mask 0x01
        sHitboxes = new int[HITBOXES.Length];
        for (int i = 0; i < HITBOXES.Length; i++) sHitboxes[i] = ue.Off(clsSoldier, "playerHitboxes_hb_" + HITBOXES[i]);

        wSpread = new int[SPREAD_WEAP_FIELDS.Length];
        for (int i = 0; i < wSpread.Length; i++) wSpread[i] = ue.Off(clsWeapon, SPREAD_WEAP_FIELDS[i]);
        cLevels = new int[SPREAD_COMP_LEVELS.Length];
        for (int i = 0; i < cLevels.Length; i++) cLevels[i] = ue.Off(clsWeaps, SPREAD_COMP_LEVELS[i]);
        cHeatUp = ue.Off(clsWeaps, "Recoil_HeatUpRate");
        cMaxLevel = ue.Off(clsWeaps, "Recoil_MaxRecoilLevel");
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
                    if (noSpread) ApplyNoSpread(me); else RestoreSpread();
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

    // Bullet dir = forward + RandomUnitVector * (1 - Accuracy), plus RecoilBulletRotOffset's
    // RandomUnitVector * trunc(Recoil_Level_R_Total * 100) * 0.01 * recoil_MasterMulti * 0.4; shotgun pellets add
    // RandomFloatInRange(-shotgunSpread, shotgunSpread) per axis. Pinning Accuracy=1, shotgunSpread=0 and the recoil
    // level at 0 zeroes every term. Only the weapon we hold is changed, so bots keep their spread.
    static void ApplyNoSpread(long me)
    {
        long wc = m.Ptr(me + sWeapComp);
        long weap = wc == 0 ? 0 : m.Ptr(wc + wcSelected);
        if (weap != spreadWeap || wc != spreadComp)
        {
            RestoreSpread(); // previous weapon (swapped, dropped, respawned)
            if (weap == 0 || !ue.IsA(weap, "BP_Weapon_C")) return;
            spreadOrig = new float[wSpread.Length];
            for (int i = 0; i < wSpread.Length; i++) spreadOrig[i] = m.F32(weap + wSpread[i]);
            spreadWeap = weap; spreadComp = wc;
        }
        for (int i = 0; i < wSpread.Length; i++)
            if (m.F32(weap + wSpread[i]) != SPREAD_WEAP_VALUES[i]) m.WriteF32(weap + wSpread[i], SPREAD_WEAP_VALUES[i]);
        // The component copies heat-up/max level from the weapon on equip, so pin its copies and levels too.
        m.WriteF32(wc + cHeatUp, 0f);
        m.WriteF32(wc + cMaxLevel, 0f);
        foreach (int o in cLevels) m.WriteF32(wc + o, 0f);
    }

    static void RestoreSpread()
    {
        if (spreadWeap == 0) return;
        for (int i = 0; i < wSpread.Length; i++) m.WriteF32(spreadWeap + wSpread[i], spreadOrig[i]);
        m.WriteF32(spreadComp + cHeatUp, spreadOrig[3]);   // Recoil_HeatUpRate
        m.WriteF32(spreadComp + cMaxLevel, spreadOrig[4]); // recoil_MaxLevel
        spreadWeap = spreadComp = 0;
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

    // Screen boxes (hitbox bounds) and labels for live enemy bots; called from the overlay thread.
    static List<EspTarget> EspTargets(int w, int h)
    {
        List<EspTarget> list = new List<EspTarget>();
        long pc = localPC;
        if (pc == 0) return list;
        long me = m.Ptr(pc + pcSoldier), cam = m.Ptr(pc + pcCamMgr);
        float[] eye = Vec(cam + camPov + povLoc), rot = Vec(cam + camPov + povRot);
        double focal = w / 2.0 / Math.Tan(m.F32(cam + camPov + povFov) * Math.PI / 360);
        double sp = Math.Sin(rot[0] * Math.PI / 180), cp = Math.Cos(rot[0] * Math.PI / 180);
        double sy = Math.Sin(rot[1] * Math.PI / 180), cy = Math.Cos(rot[1] * Math.PI / 180);
        double sr = Math.Sin(rot[2] * Math.PI / 180), cr = Math.Cos(rot[2] * Math.PI / 180);
        double[] ax = { cp * cy, cp * sy, sp };                                  // forward
        double[] ay = { sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp }; // right
        double[] az = { -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp }; // up

        long[] all = soldiers;
        int myTeam = me == 0 ? 0 : m.I32(me + sTeam);
        // Free-for-all puts everyone on one team; then every bot is an enemy.
        bool teams = false;
        foreach (long s in all) if (s != me && (m.U8(s + sPloOn) & 1) != 0 && m.I32(s + sTeam) != myTeam) teams = true;

        foreach (long s in all)
        {
            if (s == me || (m.U8(s + sIsBotOff) & sIsBotMask) == 0 || (m.U8(s + sIsDeadOff) & sIsDeadMask) != 0) continue;
            if ((m.U8(s + sPloOn) & 1) == 0 || (teams && m.I32(s + sTeam) == myTeam)) continue;
            long hp = m.Ptr(s + sHealthComp);
            int health = hp == 0 ? 0 : m.I32(hp + hpHealth);
            if (health <= 0) continue;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool onScreen = true;
            foreach (int off in sHitboxes)
            {
                long hb = m.Ptr(s + off);
                if (hb == 0) continue;
                float[] p = Vec(hb + COMPONENT_TO_WORLD + 0x10);
                double dx = p[0] - eye[0], dy = p[1] - eye[1], dz = p[2] - eye[2];
                double z = dx * ax[0] + dy * ax[1] + dz * ax[2];
                if (z < 1) { onScreen = false; break; } // behind the camera
                float x = (float)(w / 2.0 + (dx * ay[0] + dy * ay[1] + dz * ay[2]) * focal / z);
                float y = (float)(h / 2.0 - (dx * az[0] + dy * az[1] + dz * az[2]) * focal / z);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (!onScreen || minX > maxX) continue;
            // Hitbox centers sit inside the model; pad out to roughly the silhouette.
            float pad = 0.15f * Math.Max(maxX - minX, maxY - minY) + 2;

            float[] head = Vec(m.Ptr(s + sHeadHitbox) + COMPONENT_TO_WORLD + 0x10);
            double dist = Math.Sqrt(Math.Pow(head[0] - eye[0], 2) + Math.Pow(head[1] - eye[1], 2) + Math.Pow(head[2] - eye[2], 2)) / 100;
            long weap = m.Ptr(m.Ptr(s + sWeapComp) + wcSelected);
            // WeaponName holds placeholders ("Gun1"), so use the class: BP_Weap_AK_C -> AK
            string weapName = weap == 0 || !ue.IsA(weap, "BP_Weapon_C") ? "" : ue.ClassName(weap).Replace("BP_Weap_", "").Replace("_C", "");

            EspTarget t = new EspTarget();
            t.Box = new System.Drawing.RectangleF(minX - pad, minY - pad, maxX - minX + 2 * pad, maxY - minY + 2 * pad);
            t.Text = string.Format("BOT {0}hp {1:F0}m{2}", health, dist, string.IsNullOrEmpty(weapName) ? "" : " " + weapName);
            list.Add(t);
        }
        return list;
    }
}
