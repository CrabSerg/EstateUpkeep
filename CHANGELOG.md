# Changelog

## 1.0.1

Release-safety hotfix.

- Added an idempotent `OnServerInitialized` guard.
- Duplicate initialization hooks no longer create duplicate billing, repair,
  transport refresh, membership refresh or persistence timers.
- No Estate, billing, repair, transport, restart-matching or decay-protection
  behavior was otherwise changed.


### Project identity / licensing
- Added permanent `CrabSerg` copyright attribution to the source header.
- Added `A Copper Dreams project` identity.
- Added SPDX `GPL-3.0-or-later` identifier.
- Added `LICENSE.txt` and `NOTICE.txt`.
- Added author identity to `/estate help` and the startup console.
- No Core gameplay, billing, repair, transport or restart logic changed.

## 1.0.0

Initial public Core release.

### Estate
- Automatic persistent upkeep billing for eligible detached structures covered by a TC privilege zone.
- Vanilla-style building-grade/resource/tax calculations.
- Associated door/window upkeep support.
- Independent per-TC Estate model; no shared multi-TC resource pool.
- Paid Estate decay protection.
- Decay-only tracked repair debt and gradual self-repair.

### Transport
- Optional transport anti-decay for land, water and air.
- Transport protection costs zero TC resources.
- Supported modular cars, bikes/motorcycles, snowmobiles, watercraft and supported aircraft.
- Modular car lift/infrastructure false-positive filtering.
- Canonical transport-root resolution for modular-car child modules.

### Restart resilience
- Runtime transport HP checkpoints.
- Rust process-boot restart detection.
- Plugin reload vs real restart separation.
- Clean-shutdown hook no longer required for primary restart detection.
- Late-spawn reconciliation window up to 90 seconds.
- Spawn-triggered reconcile passes.
- Startup matching independent of TC privilege readiness.
- Source-aware startup damage safety.
- External sourced damage can disqualify restart healing.
- Canonical same-vehicle module/root resolution prevents false external-damage classification.

### Performance
- One full-world scan at startup.
- Event-maintained BuildingBlock, Estate candidate and transport caches.
- Recurring Estate snapshots do not enumerate `BaseNetworkable.serverEntities`.

### Administration
- Added `estateupkeep.admin` delegated admin permission.
- Public `/estate transport status`.
- Admin-only diagnostics and guarded test/migration commands.
- Cleaner player help output.
