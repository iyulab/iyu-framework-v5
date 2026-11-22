# Changelog — Iyu.Notifications

All notable changes to this package are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## How to read a release here

**`Iyu.Notifications` is versioned on its own line.** It does not reference `Iyu.Core`, Entity Framework Core or
`Iyu.MainServer`, so a change to those packages does not move this version, and a release of this package does not
move theirs. The other `Iyu.*` packages share one number among themselves (see the repository's `CHANGELOG.md`).
`Iyu.Notifications.EntityFrameworkCore` binds to Entity Framework Core and ships on that shared line; it depends on a
minimum version of this package, which its own release notes state.

## [Unreleased]

## [0.1.0] - 2026-11-22

### Added

- **First release.** Host-neutral notification routing for an application that has to tell the right people when something
  happens:
  - **Events are declared in code**, each with a key, a name and a kind — a *notice* informs and is only read; an *action
    request* is closed by being *handled*. Raising an event that was not declared is refused.
  - **Recipients come from a table the administrators edit.** A target is a kind and a value. `user` and `role` are resolved
    from the host's accounts through `INotificationIdentityDirectory`; any other kind (a department, a device) is resolved by
    an `INotificationTargetResolver` the application registers, which answers before the package's own.
  - **An inbox per recipient** with read and handled state (`INotificationInbox`), and a record of events nobody could be
    reached for (`INotificationUnreachedLog`). Nothing is dropped silently: an event with no targets, a target kind nobody
    resolves, or targets that resolve to no active user is recorded and returned to the caller.
  - **Clean-up only when asked for.** `InboxRetention` defaults to none (keep everything). A notice is removed by its creation
    time and a handled request by the time it was handled; an action request nobody has handled is never removed.
  - **The host does not start** if an event is declared twice or with a blank or too long key or name, if a port is not
    registered, or if the target table names an event nothing declares.
  - **Delivery is an extension point** (`INotificationDelivery`), called only after the inbox write; a delivery that fails is
    logged and never undoes the item. No mail or push implementation ships in this package.
  - **Payloads are opaque.** What the caller attaches is stored and returned unchanged.
