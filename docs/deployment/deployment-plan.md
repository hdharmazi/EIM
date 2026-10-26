# Non-production CI/CD plan (superseded)

**Status:** Superseded by #5246.

This page recorded the plan for the first non-production pipelines: three manual pipelines, one each for the
dashboard, the Automation host and the gateway, which deployed code only with publish profiles and a static web
app token. They were replaced by one script, `script/deployment/Deploy-Wilson.ps1`, run by the `eim` pipeline
(bat, then test) and the `eim-prd` pipeline (prd):

- [deployment.md](deployment.md): how the pipelines and the script work
- [devops-setup.md](devops-setup.md): the one-time Azure DevOps setup, including the retirement of the old
  pipeline definitions
- [production-go-live.md](production-go-live.md): standing up production

The previous version of this page is in the repository history, at the tag `pre-5246-deploy`.
