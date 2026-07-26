# THub classic release handoff

The CI artifact contains separate `THub.Web.zip`, `THub.Publications.zip`, and
`THub.Worker.zip` packages plus a SHA-256 `manifest.json`. Keep environment settings,
connection strings, encryption keys, certificates, and service credentials outside
these packages.

Run `Update-THubHosts.ps1` from an elevated Windows PowerShell task on the target
server. The script:

1. validates and expands all packages before stopping a host;
2. places both IIS applications offline, allows a short drain period, then stops their
   application pools;
3. sends a normal stop to the Worker service and refuses to replace its files if the
   service does not stop within the timeout;
4. backs up the current files, replaces all three hosts, and restarts only hosts that
   were running before the release.

Example classic-release task arguments:

```powershell
.\release\Update-THubHosts.ps1 `
  -ManifestPath .\manifest.json `
  -WebPackage .\packages\THub.Web.zip `
  -WebDirectory C:\Apps\THub\Web `
  -WebAppPool THub-Web `
  -PublicationsPackage .\packages\THub.Publications.zip `
  -PublicationsDirectory C:\Apps\THub\Publications `
  -PublicationsAppPool THub-Publications `
  -WorkerPackage .\packages\THub.Worker.zip `
  -WorkerDirectory C:\Apps\THub\Worker `
  -WorkerServiceName "THub Orchestration Worker" `
  -BackupDirectory D:\THub-Backups
```

The update intentionally does not run EF migrations. Add a separately approved classic
release task using the deployment database identity, after backup and migration review.
The current application does not define a zero-downtime, mixed-schema migration contract,
so stop all three hosts before applying a schema-changing migration.

The Worker handles a normal service stop through the .NET Generic Host, and Quartz is
configured to wait for active jobs. If the stop timeout expires, investigate the active
work instead of force-killing the process; an abrupt exit can leave at-least-once
external effects requiring reconciliation.
