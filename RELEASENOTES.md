# Release Notes

## Unreleased

- Containers no longer fail to start with `(403) Forbidden` while downloading the encryption key when Kubernetes recreates their pod (node restart, cluster start) more than 24 hours after they were created or started. The container blob SAS now lives in the container's secret, is valid for 7 days and is renewed daily by the AutoStop timer. Containers that already failed this way are redeployed with a renewed SAS automatically; existing containers move to the new setup on their next start or recovery.

- `runtests` now supports containers created with `--authenticationEmail` (AAD). Tests run through an internal, localhost-only NavUserPassword service tier (`FkhTest`) on the same database, which is created on the first run. Existing AAD containers need a restart to pick up the updated container scripts. See [AadAuthentication.md](AadAuthentication.md#running-tests).
