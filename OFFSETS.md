# pla_toon (UE 4.20) offsets

All offsets below were read from UE reflection on the running game (`bin\UeDump.exe dump` writes `dump\sdk.txt`).
The trainer resolves them by name at startup, so a game update that shifts layouts won't break it unless names change.
Most gameplay classes are **nativized Blueprints** (`DynamicClass`): reflected like Blueprints, but logic is compiled C++.

## Globals (this build)
| What | Where |
|---|---|
| GNames (`TNameEntryArray*`) | static pointer at `module+0x31A6800`; chunks of 16384, name at `FNameEntry+0x10` |
| GUObjectArray.ObjObjects | `module+0x32EBFE8`; chunked (65536/chunk), `FUObjectItem` = 24 bytes |

UObject: `+0x0C` InternalIndex, `+0x10` Class, `+0x18` FName, `+0x20` Outer. UProperty offset at `+0x44`.

## Finding the local player
`BP_platoonPlayerController_C` whose `Player` (engine `APlayerController::Player`, resolved by reflection) is non-null
→ `plo_SoldierRef` `+0x818` → your `BP_Sold2_Pawn_C`.
(`AcknowledgedPawn` is an `SCP_C` proxy pawn, not the soldier; soldiers are pooled "PLO" actors with no `Controller`.)

## BP_Sold2_Pawn_C (soldier)
| Field | Offset | Notes |
|---|---|---|
| AC_Soldier_Health | `+0x720` | component |
| AC_Soldier_Weapons | `+0x730` | component |
| AC_Soldier_Inventory | `+0x738` | component |
| V3_ZRot_Trans | `+0x3B8` | SceneComponent; `RelativeRotation.Yaw` = view yaw |
| V3_LookUpDown_Trans | `+0x818` | SceneComponent; `RelativeRotation.Pitch` = view pitch |
| playerHitboxes_hb_head | `+0x788` | StaticMeshComponent (one per body part, `+0x760..+0x7D0`) |
| isDead | `+0x889` bit 0 | |
| isBot | `+0x8A0` bit 0 | |
| Team | `+0x97C` | int |

## Health — `AC_Soldier_Health_C`
- `Health` int at `+0xF8` (100 = full).
- Damage enters via `TakeDamage` → exec thunk `0x1405C0DB0` → virtual call `vtable+0x468`
  (impl `0x140321440`, a nativized-BP jump-table state machine).
- **God mode:** the trainer gives *your* component a copied vtable whose `+0x468` slot is a `ret`, so damage never
  applies to you; bots keep the real vtable. Health is also held at 100. Restored on toggle-off / quit.

## Ammo
- Current weapon: `AC_Soldier_Weapons.SelectedWeap_R_Ref` (`+0x110`) → `BP_Weapon_C`.
- `BP_Weapon_C`: `ClipSize` `+0x3D8`, `AmmoInClip` `+0x3DC`, `Damage` `+0x3C8` (float), `ammo_TypeIndex` `+0x544`.
- Reserve: `AC_Soldier_Inventory_C.ammo` TArray<int> `+0x140`, indexed by `ammo_TypeIndex`
  (1 pistol, 2 rifle, 3 shotgun, 4 .50cal, 5 rocket). The `Ammo_1_Pistol..Ammo_5_Rocket` ints look unused.
- **Infinite ammo:** trainer keeps `AmmoInClip = ClipSize`.

## Hitboxes / head aim
- Each hitbox is a `StaticMeshComponent`; its world transform (`ComponentToWorld`, not reflected) is at `+0x180`:
  quat `+0x180`, translation `+0x190`, scale `+0x1A0`.
- Camera: `PlayerController.PlayerCameraManager` → `CameraCachePrivate.POV` (`+0x19F0`): Location, Rotation (pitch, yaw, roll).
- Aim = yaw/pitch from camera location to `hb_head` translation (+ small Z offset), written to the two rotation
  components above. The camera eases to the new rotation within ~0.5 s.
- Target filter: `isBot` set, not `isDead`, Health > 0, closest to the crosshair within the FOV cone.

## Anti-cheat note
`AC_HackerFucker_C` (on the controller) self-checks M4/AK damage, bullet speed, recoil, fire rate and player health and
reports to the server. The trainer doesn't touch weapon stats and keeps health at the normal 100.

## Bullet spread / recoil
Bullet rotation is built in the (non-nativized, bytecode) `BP_Weapon_C`:
- `GetRecoilLevel` → `trunc(AC_Soldier_Weapons.Recoil_Level_R_Total * 100)`.
- `RecoilBulletRotOffset(Rand, recoilLevel)` → `RandomUnitVectorFromStream(Rand) * recoilLevel * 0.01 * recoil_MasterMulti * 0.4`.
- `FireWeapon_SpawnBullet_v2`: normal guns `MakeRotFromX(rotVec + RandomUnitVectorFromStream(rand) * (1 - Accuracy))`;
  shotguns add `RandomFloatInRangeFromStream(-shotgunSpread, shotgunSpread)` per axis to each pellet.
- `AC_Soldier_Weapons_C.GetBulletRotBasedOnRecoil` (vtable `+0x600`, impl `0x140355780`) ignores its rand/level args and
  just returns `WeaponHolder_Trans_R` (`soldier+0x7F0`) world rotation, i.e. the aim direction.
- Recoil level: `AC_Soldier_Weapons_C` `Recoil_Level_R_FromWeap` / `_FromMovement` / `_R_Total`, plus copies of the
  weapon's `Recoil_HeatUpRate` / `Recoil_MaxRecoilLevel` (from `recoil_MaxLevel`) taken on equip.
- **No spread:** on the held weapon only, `Accuracy=1`, `shotgunSpread=0`, `movementRecoilMulti=0`, `Recoil_HeatUpRate=0`,
  `recoil_MaxLevel=0`; on our weapons component heat-up/max level and the three levels are pinned to 0. Originals are
  restored when the weapon changes or the feature is turned off. `recoil_MasterMulti` is not touched (anti-cheat checks it).

## ESP
Screen projection uses `POV.Location/Rotation/FOV` (horizontal FOV) and the 15 `playerHitboxes_hb_*` component
translations; the box is their screen bounds padded 15%. Bots are pooled: skip ones with `plo_On` (`+0x9C4`) clear.
In free-for-all every soldier has `Team = 0`, so team filtering only applies when teams differ.
`BP_Weapon_C.WeaponName` holds placeholders ("Gun1"); the class name (`BP_Weap_AK_C`) is the useful label.
