# Fkh Cheat Sheet

## Manage Containers (via CLI)

```powershell
# List your VMs
.\fkh.exe listvms

# List all VMs
.\fkh.exe listvms --all

# Create a container
.\fkh.exe createcontainer --name bcserver --artifactUrl 'https://...' --adminUsername 'admin' --adminPassword 'P@ssword1'

# Stop a container (scales deployment to 0, keeps database)
.\fkh.exe stopcontainer --name bcserver

# Start a stopped container (scales deployment back to 1)
.\fkh.exe startcontainer --name bcserver

# Remove a container (deletes deployment, service, secret, and database)
.\fkh.exe removecontainer --name bcserver
```

## Setup (for kubectl commands)

```powershell
# Get AKS credentials (one-time per machine)
az aks get-credentials --resource-group fkh-freddydk --name fkh-freddydk-aks --overwrite-existing
```

## Supported Client Versions

```powershell
# Get the protocol version and supported client versions from a running backend
$ENV:fkh_backend_URL = 'https://fkh-orgname-backend.azurewebsites.net/api'
Invoke-RestMethod -Method Get -Uri "$ENV:fkh_backend_URL/GetSupportedClientVersions"
```

The response contains the backend protocol version and the supported versions for the three clients:

```json
{
	"protocolVersion": 1,
	"clients": [
		{ "client": "VS Code extension", "version": "latest" },
		{ "client": "CLI", "version": "latest" },
		{ "client": "Web App", "version": "latest" }
	]
}
```

If a client version is `latest`, the newest available client works with that backend. If it returns a specific version number instead, that is the client version you need to use with the backend you are running.

## View Containers

```powershell
# List all BC containers
kubectl get pods -n app -l app-type=windows-servicetier -o wide

# List all services (shows public IPs and FQDNs)
kubectl get services -n app

# List all resources in the app namespace
kubectl get all -n app
```

## SQL Server

```powershell
# Check mssql pod status
kubectl get pods -n app -l app=mssql

# Check SQL disk usage
kubectl exec -n app -l app=mssql -- df -h /var/opt/mssql/data

# List databases
kubectl exec -n app -l app=mssql -- /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "SELECT name FROM sys.databases"
```

## Logs

```powershell
# Follow logs from a specific container
kubectl logs -n app -l app=freddydk-bcserver -f --tail=100

# Get mssql pod logs
kubectl logs -n app -l app=mssql --tail=100
```

## Troubleshooting

```powershell
# Describe a pod (scheduling issues, image pull errors, etc.)
kubectl describe pod <pod-name> -n app

# Get recent events
kubectl get events -n app --sort-by='.lastTimestamp' | tail -20

# Get logs from a specific service tier (by name used during create)
kubectl logs -n app -l app=freddydk-bcserver --tail=100
kubectl logs -n app -l app=freddydk-bcserver -f          # follow live

# Exec into a specific service tier pod (find pod name first, then exec)
kubectl get pods -n app -l app=freddydk-bcserver -o name
kubectl exec -it <pod-name> -n app -- powershell

# One-liner: exec into a service tier pod by label
kubectl exec -it $(kubectl get pod -n app -l app=freddydk-bcserver -o jsonpath='{.items[0].metadata.name}') -n app -- powershell

# Exec into the mssql pod
kubectl exec -it $(kubectl get pod -n app -l app=mssql -o jsonpath='{.items[0].metadata.name}') -n app -- /bin/bash

# Restart a deployment (rolling restart)
kubectl rollout restart deployment <deployment-name> -n app
```

## Manual Cleanup

```powershell
# Delete a container's resources manually (if removecontainer isn't available)
kubectl delete deployment freddydk-bcserver-deployment -n app
kubectl delete service freddydk-bcserver-service -n app
kubectl delete secret freddydk-bcserver-secret -n app

# Drop the database via exec
kubectl exec -n app -l app=mssql -- /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "DROP DATABASE [freddydk-bcserver]"
```

## Deployment

```powershell
# Deploy infrastructure (from terraform/ directory)
.\deploy.ps1 -VarFile organizations\freddydk.tfvars

# Publish function code only
cd ..\fkh-backend
dotnet publish -c Release -o bin\publish
az functionapp deployment source config-zip -g fkh-freddydk -n fkh-freddydk-functions --src bin\publish.zip
```

## Node Pool & Scaling

```powershell
# Check Windows node pool status (autoscales 0-10)
kubectl get nodes -l kubernetes.io/os=windows

# Check Linux node
kubectl get nodes -l kubernetes.io/os=linux
```
