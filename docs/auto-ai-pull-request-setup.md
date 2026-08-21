# Automatic AI-generated pull requests

The pipeline `.azure-pipelines/workflows/auto-ai-pull-request.yml` runs whenever a `feature/*` branch is pushed. It checks for an existing active PR, asks the `gpt-5.6-sol` Microsoft Foundry deployment for a title and description, and creates one draft PR to `main`.

## Security model

- Azure DevOps authentication uses the short-lived `System.AccessToken`; no personal access token is stored.
- Foundry authentication uses an API key stored as the secret pipeline variable `FOUNDRY_API_KEY`. The key is passed only as a masked environment variable and is not stored in source control.
- The script excludes common secret/configuration, binary, generated, and artifact files before sending a size-limited diff to the model.
- The model output is treated as untrusted text and used only as PR metadata.
- PRs start as drafts so a person can review the generated description.

## One-time setup

### 1. Add the Foundry API key as a secret

In Azure DevOps:

1. Open **Pipelines** and create or open the pipeline that uses `.azure-pipelines/workflows/auto-ai-pull-request.yml`.
2. Select **Edit** → **Variables** → **New variable**.
3. Set the name to `FOUNDRY_API_KEY`.
4. Paste a key from `arm-foundry-east02-resource` → **Keys and Endpoint**.
5. Select **Keep this value secret**, then save.

Do not put the key in YAML, a variable-group YAML file, scripts, local settings, or Git history. Restrict permission to edit/use the pipeline, rotate the key periodically, and replace API-key authentication with workload identity when available.

### 2. Permit the pipeline to create pull requests

In Azure DevOps:

1. Open **Project settings** → **Repositories** → `ARM.Integration.Wilson` → **Security**.
2. Locate `<Project Name> Build Service (WTC365)` (the exact prefix may vary).
3. Set these permissions to **Allow**:
   - **Contribute to pull requests**
   - **Read**
4. If your organization requires it for PR creation, also allow **Contribute**. Do not grant force-push or bypass-policy permissions.

`System.AccessToken` is mapped explicitly into the script by the YAML pipeline. No PAT variable is required.

### 3. Create the pipeline

1. Open **Pipelines** → **New pipeline**.
2. Choose **Azure Repos Git** and repository `ARM.Integration.Wilson`.
3. Choose **Existing Azure Pipelines YAML file**.
4. Select the branch containing the automation and path:
   `.azure-pipelines/workflows/auto-ai-pull-request.yml`
5. Save the pipeline.
6. Add the secret `FOUNDRY_API_KEY` variable as described above before the first feature-branch push.

### 4. Merge the automation into `main`

Azure DevOps evaluates CI YAML from the pushed branch. Ensure every new `feature/*` branch is created from a version of `main` that already contains this pipeline and script.

## Test

After setup, create a branch whose name starts with `feature/`, commit a harmless change, and push it. The pipeline should:

1. Find no existing active PR from that branch to `main`.
2. Read the masked Foundry key from the secret pipeline variable.
3. Generate the title and Markdown description.
4. Create one draft PR.

A second push to the same branch will detect the active PR and exit without creating a duplicate.

## Configuration

Pipeline variables in `.azure-pipelines/workflows/auto-ai-pull-request.yml` control:

- `FOUNDRY_ENDPOINT`
- `FOUNDRY_MODEL_DEPLOYMENT`
- `FOUNDRY_API_VERSION`
- `TARGET_BRANCH`

`FOUNDRY_API_KEY` is intentionally not declared in YAML; it must remain a secret variable in Azure DevOps.

To create active rather than draft PRs, change `-Draft $true` to `-Draft $false`.

## Troubleshooting

- **`FOUNDRY_API_KEY` is not set:** add the pipeline variable and mark it secret.
- **401 or 403 from Foundry:** confirm the key belongs to `arm-foundry-east02-resource`, then rotate and replace it if necessary.
- **403 from Azure DevOps:** grant the project Build Service identity **Contribute to pull requests** on the repository.
- **No pipeline run:** confirm the branch begins with `feature/` and contains the YAML file.
- **PR already exists:** expected behavior; the script intentionally avoids duplicates.
