# Voxelfield

Voxelfield is a multiplayer shooter game where the terrain is completely destructible.

Video: https://youtu.be/m-gB7DvBCo4

![Screenshot 1](screenshot_1.jpg)
![Screenshot 2](screenshot_2.jpg)

### Technical details
- Written in C# using the Unity game engine and LiteNetLib networking library
- Authoritative network model supporting 16 players with 120 packets per second
- Custom hybrid ECS system to ensure determinism
- Integration with AWS GameLift, authentication with Steam API, stat tracking with AWS DynamoDB

This is by far my biggest project, `cloc` output (counts lines of code):

```
-------------------------------------------------------------------------------
Language                     files          blank        comment           code
-------------------------------------------------------------------------------
C#                             218           3221           4791          19558
-------------------------------------------------------------------------------
```

### Gamemodes
- CTF (Capture the Flag)
- Secure Area
- Deathmatch

### Weapons
- Rifle
- Deagle
- Sniper
- Grenade Launcher
- Missile Launcher
- Sand Bomb
- C4
- Grenade
- Molotov
- Shotgun
- Pickaxe
- Pistol
- SMG
- Impact Grenade
- Boomstick
- Flashbang
