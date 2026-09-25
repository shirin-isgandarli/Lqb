# Lqb: EF Core model changes as Liquibase changesets

`dotnet lqb add <Name>` works like `dotnet ef migrations add`, but writes a Liquibase
changeset (forward SQL + rollback) instead of a C# migration. Liquibase then applies it.

## Using it in an app

```bash
dotnet tool install --global Lqb.Tool
dotnet add MyApp package Lqb.Design          # the startup (executable) project
dotnet lqb add AddCustomerEmail -p MyApp.Data -s MyApp
liquibase update --changelog-file=MyApp.Data/db/changelog/db.changelog-master.xml
```

Each run writes:

- `db/changelog/changes/<timestamp>_<Name>.sql`: a formatted-SQL changeset with `--rollback` lines
- `db/changelog/db.changelog-master.xml`: the new file is added as an `<include>`
- `Migrations/<Context>ModelSnapshot.cs`: EF's model snapshot, used to compute the next diff.
  Use `-o Data/Migrations` to put it elsewhere (relative to the project; the snapshot is
  moved there if it already exists, and new snapshots get a matching namespace)

Commit all three. Never run `dotnet ef database update`; Liquibase is the only thing
that changes the database.

## How it works

| Part | Package | Job |
|---|---|---|
| `Lqb.Tool` | .NET tool | CLI. Builds the project, reads MSBuild paths, launches the host |
| `Lqb.Host` | inside the tool | Runs via `dotnet exec` with the **app's** deps.json/runtimeconfig, then calls Lqb.Design |
| `Lqb.Design` | referenced by the app | Uses EF's model differ + SQL generator, writes changesets and the snapshot |

The host trick is the same one `dotnet-ef` uses: the code that touches EF Core runs
with the app's own EF Core and provider versions, so the tool never ships a clashing copy.

## Current limitations (v1)

- DbContext must come from an `IDesignTimeDbContextFactory<T>` or a parameterless constructor
- The startup project must be executable (console/web), like dotnet-ef
- Formatted SQL only; native Liquibase change types (addColumn, ...) are a possible v2
- Custom `IDesignTimeServices` in the app are not applied yet
- No `remove` command yet
- Existing EF-migrated databases need a Liquibase baseline first (e.g. `changelog-sync`)

## Build and pack

```bash
dotnet pack src/Lqb.Tool -c Release -o nupkg
dotnet pack src/Lqb.Design -c Release -o nupkg
dotnet tool install --global Lqb.Tool --add-source ./nupkg
```
