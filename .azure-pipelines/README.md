# Azure DevOps pipelines

## Deployment workflows

| Workflow | Trigger | Purpose |
|---|---|---|
| `workflows/deployment/deploy-eim-customer-nonprod.yml` | Manual | Build once and promote Wilson.Integration.Automation through `dev`, `bat`, and `test` |
| `workflows/deployment/deploy-d365fsc-gateway-nonprod.yml` | Manual | Test and deploy the shared nonprod Gateway |
| `workflows/deployment/deploy-dashboard-nonprod.yml` | Manual | Deploy the shared Dashboard API, frontend, or both |
| `workflows/auto-ai-pull-request.yml` | `feature/*` push | Create a draft pull request with Microsoft Foundry-generated metadata |

Configure deployment secrets, notification identities, Azure DevOps Environments, and SCM publishing credentials before running a deployment workflow. See [Azure deployment pipelines setup](../docs/azure-deployment-pipelines-setup.md).
