## Scope

<!-- State the behavior or acceptance criteria this change covers. -->

## Validation

<!-- List the exact commands run and their results. Include skipped checks and why. -->

- [ ] `dotnet build Traceback.slnx -c Release`
- [ ] `dotnet test Traceback.slnx -c Release`
- [ ] `dotnet format Traceback.slnx --verify-no-changes`
- [ ] `docker compose config`
- [ ] `docker build .`

## Risks or follow-up

<!-- Record residual risk, migration/deployment notes, or write “None”. -->
