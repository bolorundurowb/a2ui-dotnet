# Repository instructions

## Project direction

- Use Nigerian naira (NGN, ₦) as the first currency in repository-owned examples and currency lists.

- Read `A2UI-DOTNET-PLAN.md` before making protocol, package, or platform decisions. Treat proposed choices and open questions as planning input until the maintainer settles them.
- Preserve the dependency direction from Agent to Core. Keep Core usable in headless .NET applications, and keep model-provider and transport dependencies at the application or adapter boundary.
- Implement A2UI functionality in-house and prefer the .NET base class library. A third-party library may be added to an SDK package only when a measurement shows a clear benefit over the in-house or base class option. It must be MIT-licensed, its transitive dependencies must be MIT-licensed, and it must support trimming and Native AOT, verified in a clean trimmed consumer. Record the measurement and licence check in the PR. Preserve the original licenses for upstream specification and test assets.
- Start with a shared `net8.0` baseline and verify .NET 8 through 11. Use `net9.0` as the fallback if the .NET 8 feasibility check identifies a blocker. Keep runtime-specific paths justified by measured results or required APIs.
- Keep SDK package versions independent of A2UI wire versions. Document any protocol, inference format, catalog, or platform claim with its verification status.
- Prefer en-NG or en-GB for new work, documentation etc, only public API methods should be in en-US to match the official SDK. If is acceptable to ignore this rule if the official SDK uses for en-US for the section to be named. For existing files, when any area is being touched, use that opportunity to fix the language

## Protocol and tests

- Implement against the pinned upstream protocol, schemas, and [module blueprints](https://github.com/a2ui-project/a2ui/tree/main/blueprints/modules). Record the revision and a minimal fixture when upstream references disagree.
- Run the official [Core suite](https://github.com/a2ui-project/a2ui/tree/main/conformance/core) and [Agent suite](https://github.com/a2ui-project/a2ui/tree/main/conformance/agent) through the SDKs' public entry points. Keep both suites pinned to the same upstream revision. Require all applicable cases to pass for release and record the reason for any exclusion. Add focused regressions for behavior the fixtures miss.
- Run upstream scaling cases and benchmark hot paths before adding optimization complexity. Prefer incremental updates and clear ownership of JSON values and buffers.
- Validate packaging and publish behavior in a clean consumer. Report the build, test, and publish checks that ran and any outstanding verification.
- Keep upstream fixture provenance, license headers, and required notices when importing files. The repository's own code uses MIT.

## Working in this repository

- Document every public SDK API with XML documentation, including public types, members, enum values and parameters where applicable.
- Follow `.editorconfig` and keep changes focused. Add build and test commands to `CONTRIBUTING.md` as the corresponding projects become available.
- Use `.ignore` for routine repository searches and file discovery. `rg` reads it automatically. Inspect a build artifact or lockfile directly when the task requires it.
- Keep generated build output, local settings, and signing material outside source control. Commit sample configuration with placeholder values only.
- When work on an issue or PR is done, update the issue or PR with what landed. Check off each acceptance criterion or checklist item that is met, and leave unmet items unchecked with the reason. Move unfinished work to a follow-on issue and link it. Do not check off items that are not verified.

## Git conventions for agents

### Branches

- Use `master` as the default PR base. Use the branch or base specified by the user when they provide one.
- When creating a task branch, use `<type>/<short-slug>`, such as `feat/catalog-validation` or `fix/json-pointer-escaping`. Use lowercase words separated by hyphens. Include the issue number in the slug when it helps identify the task, for example `fix/42-json-pointer-escaping`.
- Do not use LLM, model, or coding-agent names as branch prefixes, including `codex/` and `claude/`.
- Use a separate branch for each issue by default. Share a branch only when the changes need to be reviewed or landed together.
- Reuse an appropriate task branch when one is already checked out. Preserve unrelated working-tree changes.

### Commits

- Use `<type>(<scope>): <imperative summary>`. The scope is optional for repository-wide changes. Common scopes are `core`, `agent`, `conformance`, and `repo`.
- Use the type that best describes the change: `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci`, or `chore`.
- Keep each commit focused and stage only files belonging to that change. Use the body for rationale or compatibility details that the subject cannot convey.
- Examples include `feat(core): validate catalog references`, `perf(agent): resume parsing from the last chunk`, and `docs(repo): explain release requirements`.

### Pull requests

- Use the commit subject format for the PR title and follow `.github/PULL_REQUEST_TEMPLATE.md` for the body. Identify the behavior change, why it is needed, the checks that ran, and any material verification gaps.
- Use `Closes #<issue-number>` in the PR body by default for each issue the PR addresses. Evaluate each issue independently against its acceptance criteria, required verification, and any remaining work. If that review shows an issue should stay open, use a plain issue reference and explain what remains. In a PR covering multiple issues, use the closing keyword for every issue that is ready to close.
- Include the protocol version and upstream fixture for interoperability changes, consumer impact for public API changes, and before-and-after measurements for performance changes.
- When authorized to open a PR, open it ready for review unless verification or required decisions are still pending. Use a draft only in that case, and move it to ready for review once those items are settled. Push and open the PR only within the user's authorization. Leave merging and history rewrites to explicit user direction.
