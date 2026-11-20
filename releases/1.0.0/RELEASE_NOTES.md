# AutoCommit v1.0.0

AutoCommit combines selected Git or TFVC changes, project commit rules, and an OpenAI compatible model service to prepare a commit message for review.

## Supported editors

| Editor | Repository support | After review |
| --- | --- | --- |
| Visual Studio 2019 (16.x) | Git and TFVC | Confirm and commit locally to Git or check in to TFVC |
| Visual Studio 2022 17.14 | Git and TFVC | Confirm and commit locally to Git or check in to TFVC |
| VS Code desktop | Git | Fill the native Git commit box; the user commits with VS Code SCM |

Each host requires its own VSIX. The packages are not interchangeable.

## Features

- **Accurate change scope:** Git uses the index, including partially staged changes. In VS Code, an empty index with pending changes triggers a confirmation before staging all changes. In Visual Studio, TFVC reads the intersection of the selected mapped directory and Team Explorer's Included Changes.
- **Project specific generation:** Configure an OpenAI compatible base URL and model, request timeout, input budget, optional `EnableThinking`, and HTTP allowance. Project `.auto/model.json` and `.auto/commit-rules.md` take precedence over defaults.
- **Large diff handling:** Extract per file facts within the request budget, retain source identifiers, compact adjacent summaries to fit the final request, and generate the message using project rules. Timed out requests can be retried or split into smaller batches. If generation cannot finish, AutoCommit offers a partial summary with uncovered files identified, or a local summary when no model facts were extracted.
- **Draft and scope protection:** Check the selected change snapshot before and after generation. Visual Studio checks again before commit or check in. Canceling, a changed snapshot, or declining replacement keeps the existing draft. Git hooks and signing continue to run through Git.
- **Diagnostics:** Show progress, stages, request time, and returned token usage. Review run logs in VS Code Output or Visual Studio model settings. Connection tests send fixed text; the VS Code sample generation command uses fixed sample changes and does not send repository content.
- **Controlled data and writes:** Generation sends the selected changes and project rules to the configured model service. HTTP requires explicit opt in. AutoCommit does not push commits or bypass TFVC team policies.

## Quick start

1. Install the VSIX for your editor and restart it.
2. Configure the model service URL, deployed model name, and API key. HTTP services require explicit permission. Project `.auto/model.json` values take precedence when present.
3. Stage the Git changes to include. For TFVC, first move the intended files into Team Explorer's Included Changes.
4. Read changes, generate the message, review it, and commit using the editor workflow.

API keys stay on the local machine: Visual Studio uses Windows DPAPI; VS Code uses SecretStorage. Verify that your configured model service is appropriate for the code you send. Automatic secret scanning is not included.

## Validation and known limits

- Visual Studio Release rebuild: 0 warnings, 0 errors.
- .NET automated tests: 33 passed, 0 failed.
- VS Code Node tests: 19 passed, 0 failed.
- All three VSIX package versions and SHA-256 checksums were verified.
- Editor GUI acceptance and integration with a real TFVC server were not performed as part of this automated release. Validate installation, generation, commit scope, and team policies in your target environment.
- The VS Code packager warned that the repository does not include a LICENSE file; packaging succeeded.

Download the three VSIX files from this release. Checksums are attached as `SHA256SUMS.txt`.
