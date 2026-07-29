## Summary

- 

## Validation

- [ ] `dotnet build Devwt.slnx --no-restore -warnaserror`
- [ ] `dotnet format Devwt.slnx --verify-no-changes --no-restore`
- [ ] `dotnet test Devwt.slnx --no-restore`
- [ ] VM install/uninstall smoke test, if installer or Sandboxie behavior changed

## Safety

- [ ] No generated artifacts, certificates, logs, secrets, or private lab files are committed
- [ ] Kernel-mode changes keep policy decisions in user mode
- [ ] New listeners are localhost-only by default
