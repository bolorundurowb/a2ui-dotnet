# Upstream A2UI baseline

[The manifest](a2ui-baseline.json) pins the protocol, schemas, Basic Catalog and examples, candidate optional catalogs, module blueprints, Core and Agent conformance suites and shared fixture data to a single commit. Both Python peer SDK release tags resolve to that commit. Their released packages are designated for interoperability verification. The tag check establishes source provenance. Running the peer packages is part of the interoperability milestone.

## Fetch and refresh

Run `./scripts/Get-UpstreamBaseline.ps1` with PowerShell, Git and tar. It fetches the locked commit, verifies each Git SHA-1 object ID and type, and extracts the selected assets under `artifacts/upstream/`. A Git tree ID covers the tracked filenames and descendant contents. Using Git archive preserves tracked file bytes and avoids checkout line-ending conversion.

On refresh, choose a commit and resolve peer release tags to their peeled commit IDs. Update version and license information, then collect replacement IDs with `git ls-tree <commit> -- <path>`. Run the fetch script from a clean checkout, inspect the upstream changes and update the [compatibility matrix](../docs/compatibility.md) and any affected regression expectations. Advance Core and Agent fixtures together.

## License and attribution

The A2UI project publishes these assets under Apache License 2.0. The fetch includes the upstream root license and copies each selected path with its attribution and existing headers intact. This repository's authored code is MIT-licensed. If upstream assets are redistributed, include the upstream license and any applicable notices alongside them. [Pinned upstream license](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/LICENSE)

## Resolving disagreements

Use the versioned protocol for wire semantics, its schemas for shape, and the module blueprints and suites for SDK behavior. Compare conflicting cases with the pinned peer SDKs. Record the source locations, smallest reproducing fixture, observed behavior and chosen implementation rule before making a compatibility claim. If a case cannot be reconciled, record the reason and release impact with the suite result.

The pinned protocol describes lifetime-unique surface IDs and also describes the duplicate error in terms of a surface that has yet to be deleted. The [create/delete/create input](surface-id-reuse.json) was replayed through the released Python Core 0.7.0 `MessageProcessor.process_messages` with the v1.0 Basic Catalog. After each message the live surface IDs were respectively `["form"]`, `[]`, and `["form"]`. The .NET package smoke check verifies the same lifecycle. Generated messages use fresh IDs. [Protocol text](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/specification/v1_0/docs/a2ui_protocol.md#createsurface) · [Python Core creation check](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/python/a2ui_core/src/a2ui/core/processing/message_processor.py#L775-L780)

The pinned composition scaling catalog uses `catalogId: "conformance/composition"` and a published URI for `$id`. Core uses the first for registry lookup and the second for self-references. The same fixture has no functions and omits the schema-required `$defs.anyFunction` member. The scaling adapter supplies `"anyFunction": false` in memory and leaves the pinned asset intact. The catalog attributes fixture uses a similarly abbreviated function definition without the required schema fields and is excluded from the registration checks. [Composition fixture](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/test_data/catalogs/composition_catalog_v1_0.json) · [Catalog schema](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/specification/v1_0/json/catalog_definition.json)
