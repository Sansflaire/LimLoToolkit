# Two builds: public and dev

LimLoToolkit ships as two different binaries from one source tree.

| | Dev | Public |
|---|---|---|
| Configuration | `Debug` | `Release` |
| Built by | `dotnet build -c Debug`, copied into `devPlugins/LimLoToolkit/` | CI, on every push to `main` |
| Reaches | Trist's machine | the `pluginmaster.json` link his friends install from |
| Trainer compiled in | yes | **no** |
| Recording code compiled in | yes | **no** |
| Mobs shown | everything measured or seen | only mobs with **confirmed (locked)** values |
| Live Mode switch | yes | n/a — always live |

## How the split is enforced

`src/LimLoToolkit.csproj`:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <DefineConstants>$(DefineConstants);PUBLIC_BUILD</DefineConstants>
</PropertyGroup>

<ItemGroup Condition="'$(Configuration)' == 'Release'">
  <Compile Remove="Tools\AggroTrainer.cs" />
  <Compile Remove="Tools\AggroLearningRecording.cs" />
</ItemGroup>
```

CI already ran `dotnet build -c Release`, so no workflow change was needed and
every published release is automatically the stripped one.

Two whole files are dropped rather than fenced:

- `AggroTrainer.cs` — the sampler. Everything that watches for pulls.
- `AggroLearningRecording.cs` — `AddSample`, `AddSafeObservation`, `AddSighting`,
  as a `partial` half of `AggroLearningStore`.

The recording methods were originally fenced with `#if` inside
`AggroLearning.cs` and that failed: the read side and the write side are
interleaved there, so the fences also cut out `WidestPullAngle`, `RangeSolved`
and `ShapeSolved`, which the drawing path needs. **If a new store method is
needed by classification or drawing, it belongs in `AggroLearning.cs`, not in the
recording file** — otherwise the public build stops compiling.

## `BuildFlavor.IsLive` vs `#if PUBLIC_BUILD`

Both are needed and they are not interchangeable.

```csharp
#if !PUBLIC_BUILD          // keeps the code out of the public binary
    if (!BuildFlavor.IsLive)   // makes Live Mode preview correctly in the dev build
    {
        ...training UI...
    }
#endif
```

- **`BuildFlavor.IsLive`** is the predicate UI code asks. It is `true` in the
  public build unconditionally, and follows `Configuration.LiveMode` in the dev
  build. Testing `#if PUBLIC_BUILD` to decide what to *draw* breaks Live Mode
  preview — the dev build would keep drawing the training UI.
- **`#if !PUBLIC_BUILD`** is what actually removes it from the shipped DLL. The
  runtime check alone leaves unreachable IL and its strings in the binary.

Guarding with only one of the two is the mistake to watch for.

## Live Mode

`Settings → LIVE MODE` (dev build only, first thing in the window). It makes the
dev plugin behave exactly like the public one: recording stops, the measurement
panels disappear, and only confirmed mobs are listed or drawn. It exists so the
shipped experience can be checked without a rebuild and a reinstall.

It is a display and behaviour switch, not a data switch — nothing stored is
touched, so turning it off restores the full picture immediately.

## What "confirmed" means

A mob is confirmed when its profile has `Locked = true`, set by hand in the dev
build's Mob Viewer (**Set values by hand → Lock**). The public build has no lock
controls; it reads the flag and presents those values as settled.

**This makes locking the publication step.** A mob that has been measured to
death but never locked does not appear in the public build at all. The shipped
`src/Data/aggro-seed.json` currently carries **5** locked mobs out of 103
profiles — see the count printed when refreshing the seed, and lock more before
telling anyone the public plugin is worth installing.

## Seeding confirmed values to existing users

`MergeSeedData` adopts a seed profile's locked values onto a user's existing
profile when the seed is locked and theirs is not. Without that, only brand-new
installs would ever receive newly confirmed mobs — an existing user who had
walked past a mob once already has a profile for it, which would block the
confirmed numbers forever, and in the public build that mob would silently
vanish from the list for exactly the people who play most.

A locally locked profile is never overwritten. The user's own confirmation
outranks anything baked in at build time.
