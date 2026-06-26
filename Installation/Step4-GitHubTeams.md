# Step 4 — Set Up GitHub Teams for Authorization

> **Performed by:** GitHub Organization Administrator

Fkh uses GitHub team membership to decide who can provision containers and who has admin access.

Terraform does not create or manage these teams. You create the teams in GitHub, then reference them in `deployment.tfvars`.

> **Already have teams from another Fkh deployment?** You can reuse them. Skip to [4.5 — Update deployment.tfvars](#45--update-deploymenttfvars) and reference the existing team names.

## Access model

| Team type | Purpose | Required? |
|---|---|---|
| Member team | Users who can provision Business Central containers | Yes |
| Admin team | Users with admin access; they also get normal member access | Optional |
| Support team | Users with support access; permissions are enforced in the backend | Optional |

---

## 4.1 — Create the member team

1. Open this URL, replacing `YOUR-ORG` with your GitHub organization name:

   ```text
   https://github.com/orgs/YOUR-ORG/new-team
   ```

2. Enter a team name, for example `Fkh-members`.
3. Choose **Visible** or **Secret** visibility.
4. Select **Create team**.
5. Add the users who should be allowed to provision Business Central containers.

## 4.2 — Create the admin team

Skip this section if you do not need a separate admin tier.

1. Open this URL, replacing `YOUR-ORG` with your GitHub organization name:

   ```text
   https://github.com/orgs/YOUR-ORG/new-team
   ```

2. Enter a team name, for example `Fkh-admins`.
3. Select **Create team**.
4. Add the users who should have admin access.

Admin team members automatically receive normal user access. You do not need to add them to both teams.

## 4.3 — Create the support team (optional)

Skip this section if you do not need a separate support tier.

1. Open this URL, replacing `YOUR-ORG` with your GitHub organization name:

   ```text
   https://github.com/orgs/YOUR-ORG/new-team
   ```

2. Enter a team name, for example `Fkh-support`.
3. Select **Create team**.
4. Add the users who should have support access.

Support permissions are defined in the Fkh backend. Support users are flagged with `_isSupport` on each request so you can add operation-specific rules later.

## 4.4 — Optional: allow teams from another organization

You can grant access to teams in other GitHub organizations. Add each organization and team pair to `allowed_org_teams` in `deployment.tfvars`.

Example:

```hcl
allowed_org_teams = [
  { org = "my-company",  team = "Fkh-members" },
  { org = "partner-org", team = "BC-developers" }
]
```

The administrator of each GitHub organization is responsible for creating the team and managing its members.

## 4.5 — Update deployment.tfvars

In your deployment repository, open `config/deployment.tfvars` and set the team references.

```hcl
# Member teams: users in these teams can provision containers
allowed_org_teams = [
  { org = "my-company", team = "Fkh-members" }
]

# Admin teams: users in these teams get admin access and normal access
admin_org_teams = [
  { org = "my-company", team = "Fkh-admins" }
]

# Support teams: users in these teams get support access
support_org_teams = [
  # { org = "my-company", team = "Fkh-support" }
]

# Explicit users: grant access by GitHub username (role = admin, member, or support)
allowed_users = [
  # { user = "octocat", role = "member" }
]
```

> **Important:** `org` and `team` values are case-sensitive. They must match the GitHub organization and team names exactly.

## Step complete

GitHub team-based authorization is now ready. You will finish configuring these values in Step 5.

---

*Previous: [Step 3 — Create the GitHub App](Step3-GitHubApp.md)*  
*Next: [Step 5 — Configure Your Environment](Step5-ConfigureEnvironment.md)*
