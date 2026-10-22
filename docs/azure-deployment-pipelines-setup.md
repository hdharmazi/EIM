# Azure DevOps pipeline setup

Moved. Non-production deployment is now one pipeline, `pipelines/eim.yml`, running
`script/deployment/Deploy-Wilson.ps1` through a workload identity service connection. No publish profile or
static web app token is stored.

- One-time setup (environments, service connection, variable group, creating the pipeline):
  [deployment/devops-setup.md](deployment/devops-setup.md)
- How the pipeline and script work: [deployment/deployment.md](deployment/deployment.md)
- Retiring the three earlier pipeline definitions: [deployment/devops-setup.md](deployment/devops-setup.md),
  section 8
