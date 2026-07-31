## Summary

- 

## Validation

- [ ] `dotnet build Devwt.slnx --no-restore -warnaserror`
- [ ] `dotnet format Devwt.slnx --verify-no-changes --no-restore`
- [ ] `dotnet test Devwt.slnx --no-restore`
- [ ] Windows install/uninstall validation, if installer behavior changed

## Safety

- [ ] No generated artifacts, certificates, logs, secrets, or machine-specific files are committed
- [ ] New listeners are localhost-only by default
