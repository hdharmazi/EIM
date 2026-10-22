# Non-production CI/CD plan (superseded)

**Status:** Superseded by #5246.

This page recorded the plan for the first non-production pipelines: three manual pipelines, one each for the
dashboard, the Automation host and the gateway, which deployed code only with publish profiles and a static web
app token. They were replaced by a single pipeline and script:

- [deployment.md](deployment.md): how `pipelines/eim.yml` and `script/deployment/Deploy-Wilson.ps1` work
- [devops-setup.md](devops-setup.md): the one-time Azure DevOps setup
- [resource-rename-cutover.md](resource-rename-cutover.md): the move to the new resources, and the retirement of
  the old pipeline definitions

The previous version of this page is in the repository history, at the tag `pre-5246-deploy`.
