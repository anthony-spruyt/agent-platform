# Changelog

## [1.20.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.20.0...v1.20.1) (2026-10-28)


### Bug Fixes

* **triage:** auto-approve Docker Hub bumps of our own images ([#197](https://github.com/anthony-spruyt/agent-platform/issues/197)) ([8ed0d76](https://github.com/anthony-spruyt/agent-platform/commit/8ed0d7694aee7386d1ac4935d2f512c7b4ed1faf))

## [1.20.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.19.1...v1.20.0) (2026-10-28)


### Features

* **triage:** put Renovate security PRs ahead of the Claude caps ([#188](https://github.com/anthony-spruyt/agent-platform/issues/188)) ([5972d9b](https://github.com/anthony-spruyt/agent-platform/commit/5972d9b9dbc195317479550570b9be094ae9cd2b))
* **triage:** say where the notes came from in a changelog-present approval ([#189](https://github.com/anthony-spruyt/agent-platform/issues/189)) ([79776c4](https://github.com/anthony-spruyt/agent-platform/commit/79776c43b496882082d5fe364c3f0b38baadf859))

## [1.19.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.19.0...v1.19.1) (2026-10-28)


### Bug Fixes

* **deps:** update aspire monorepo to 13.6.2 ([#171](https://github.com/anthony-spruyt/agent-platform/issues/171)) ([d3d6910](https://github.com/anthony-spruyt/agent-platform/commit/d3d69103730c601c56439c3d7903c76be441ee98))
* **deps:** update opentelemetry-dotnet monorepo to 1.20.1 ([#172](https://github.com/anthony-spruyt/agent-platform/issues/172)) ([f8cb36d](https://github.com/anthony-spruyt/agent-platform/commit/f8cb36dc04ef8d43bf5e2c390f858375592f0a65))
* **deps:** update temporalio-dotnet to 1.21.0 ([#176](https://github.com/anthony-spruyt/agent-platform/issues/176)) ([4525abe](https://github.com/anthony-spruyt/agent-platform/commit/4525abe90065141ca03625ad836707efb13452fe))
* **triage:** accept dot-less, prefixed, suffixed, range and PEP 440 versions as version-only ([#181](https://github.com/anthony-spruyt/agent-platform/issues/181)) ([cf8efde](https://github.com/anthony-spruyt/agent-platform/commit/cf8efde69b94b683f70163715d46b3e84059cab5))

## [1.19.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.18.0...v1.19.0) (2026-10-26)


### Features

* **triage:** read the Jev path and model from config ([#157](https://github.com/anthony-spruyt/agent-platform/issues/157)) ([9694aff](https://github.com/anthony-spruyt/agent-platform/commit/9694aff8f7ee25a163bb35ef6a8e5457a279860d))

## [1.18.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.17.2...v1.18.0) (2026-10-25)


### Features

* **triage:** leave blocked PRs out of the pending count ([#151](https://github.com/anthony-spruyt/agent-platform/issues/151)) ([8ed2459](https://github.com/anthony-spruyt/agent-platform/commit/8ed245974a9927f41548eb53fd859e9c178da7d1))

## [1.17.2](https://github.com/anthony-spruyt/agent-platform/compare/v1.17.1...v1.17.2) (2026-10-24)


### Bug Fixes

* **deps:** update dependency kubernetesclient to v21 ([#128](https://github.com/anthony-spruyt/agent-platform/issues/128)) ([66e8e25](https://github.com/anthony-spruyt/agent-platform/commit/66e8e25f04f6251ff5e91f0191d54f0fe16283e2))
* **deps:** update dotnet monorepo ([#129](https://github.com/anthony-spruyt/agent-platform/issues/129)) ([bbacb4e](https://github.com/anthony-spruyt/agent-platform/commit/bbacb4e5f3a93f761cd053d2048492171148947f))
* **deps:** update opentelemetry-dotnet monorepo to 1.20.0 ([#134](https://github.com/anthony-spruyt/agent-platform/issues/134)) ([48ef980](https://github.com/anthony-spruyt/agent-platform/commit/48ef9801268b9c8a533381247bc698ef2e4164b4))
* **deps:** update opentelemetry-dotnet-contrib monorepo to 1.20.0 ([#133](https://github.com/anthony-spruyt/agent-platform/issues/133)) ([a527e74](https://github.com/anthony-spruyt/agent-platform/commit/a527e7424858b4a250459608ff3d54d0faf211f5))

## [1.17.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.17.0...v1.17.1) (2026-10-20)


### Bug Fixes

* **triage:** reword submit_result rejection messages as plain facts ([#121](https://github.com/anthony-spruyt/agent-platform/issues/121)) ([f15d64c](https://github.com/anthony-spruyt/agent-platform/commit/f15d64c14f674a7248fbfbe5b4cb37207efa9b2a))

## [1.17.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.16.0...v1.17.0) (2026-10-19)


### Features

* **triage:** detect release notes in code before Jev ([#117](https://github.com/anthony-spruyt/agent-platform/issues/117)) ([5365c78](https://github.com/anthony-spruyt/agent-platform/commit/5365c78217233fa8018be0cc1e26c02723e47d89))

## [1.16.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.15.2...v1.16.0) (2026-10-19)


### Features

* **triage:** give the model the CI state instead of raw checks ([#113](https://github.com/anthony-spruyt/agent-platform/issues/113)) ([7d44ae6](https://github.com/anthony-spruyt/agent-platform/commit/7d44ae64cc4ddb1e348b1e492b0e1fe5352e9f9d))

## [1.15.2](https://github.com/anthony-spruyt/agent-platform/compare/v1.15.1...v1.15.2) (2026-10-19)


### Bug Fixes

* **triage:** drop only cancelled runs superseded by a newer workflow run ([#109](https://github.com/anthony-spruyt/agent-platform/issues/109)) ([24007f6](https://github.com/anthony-spruyt/agent-platform/commit/24007f66f3897d573dde04c3b80466f161679fc6))

## [1.15.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.15.0...v1.15.1) (2026-10-18)


### Bug Fixes

* **deps:** pin MessagePack to a patched 2.5 release ([#105](https://github.com/anthony-spruyt/agent-platform/issues/105)) ([e9e4ff2](https://github.com/anthony-spruyt/agent-platform/commit/e9e4ff263e2ca33e9ea557c223eee0a56e4e4fa1))
* **triage:** accept pip hash lines, shortened versions and go indirect bumps as version-only ([#104](https://github.com/anthony-spruyt/agent-platform/issues/104)) ([707ec1e](https://github.com/anthony-spruyt/agent-platform/commit/707ec1e1db177e237aef11d6f02816edbaa86343))

## [1.15.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.14.2...v1.15.0) (2026-10-15)


### Features

* **triage:** show the queue cause in the check title and clear old verdict labels ([#98](https://github.com/anthony-spruyt/agent-platform/issues/98)) ([4d198ad](https://github.com/anthony-spruyt/agent-platform/commit/4d198adcb516b83d86c7a25f566f3c9305a8e567))

## [1.14.2](https://github.com/anthony-spruyt/agent-platform/compare/v1.14.1...v1.14.2) (2026-10-15)


### Bug Fixes

* **triage:** accept suffixed image tag bumps when the commit table omits the suffix ([#94](https://github.com/anthony-spruyt/agent-platform/issues/94)) ([12b33c3](https://github.com/anthony-spruyt/agent-platform/commit/12b33c317ed896492ff777a2436b623e8f8bb30b))

## [1.14.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.14.0...v1.14.1) (2026-10-15)


### Bug Fixes

* **deps:** update dotnet monorepo ([#88](https://github.com/anthony-spruyt/agent-platform/issues/88)) ([a09e4b2](https://github.com/anthony-spruyt/agent-platform/commit/a09e4b258b36f50dde0d25b8bef4d99866ce0ff0))

## [1.14.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.13.0...v1.14.0) (2026-10-15)


### Features

* **triage:** say when a high-risk SAFE PR needs approval ([#89](https://github.com/anthony-spruyt/agent-platform/issues/89)) ([2e53f2b](https://github.com/anthony-spruyt/agent-platform/commit/2e53f2b04109ac5c449721c035d1ce2d6b980d17))

## [1.13.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.12.0...v1.13.0) (2026-10-15)


### Features

* **triage:** accept commits that only merge the base branch in ([#82](https://github.com/anthony-spruyt/agent-platform/issues/82)) ([4c4b701](https://github.com/anthony-spruyt/agent-platform/commit/4c4b70190bfa0eb707d0e82ecfdcbfdffee43b5f))

## [1.12.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.11.1...v1.12.0) (2026-10-14)


### Features

* **triage:** fresh check on re-triage, no hand-off on Renovate's own gates ([#76](https://github.com/anthony-spruyt/agent-platform/issues/76)) ([e6b49f0](https://github.com/anthony-spruyt/agent-platform/commit/e6b49f03715fa0ee0f97f7c189399f997402782b))

## [1.11.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.11.0...v1.11.1) (2026-10-14)


### Bug Fixes

* **triage:** skip repos with pull requests turned off in the sweeper ([#74](https://github.com/anthony-spruyt/agent-platform/issues/74)) ([3e1c567](https://github.com/anthony-spruyt/agent-platform/commit/3e1c567bc6382f839c5e1d10315448259f894536))

## [1.11.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.10.0...v1.11.0) (2026-10-14)


### Features

* **triage:** re-drive parked Renovate PRs with a sweeper ([#72](https://github.com/anthony-spruyt/agent-platform/issues/72)) ([c4da42a](https://github.com/anthony-spruyt/agent-platform/commit/c4da42a067c5abb6b0fe4890573593bf6914e1f0))

## [1.10.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.9.1...v1.10.0) (2026-10-14)


### Features

* **triage:** ask Claude about Renovate PRs the rules can't decide ([2cfb36e](https://github.com/anthony-spruyt/agent-platform/commit/2cfb36ef9a1b0ff6982a6df231b14e8598bfba67))

## [1.9.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.9.0...v1.9.1) (2026-10-14)


### Bug Fixes

* **triage:** read the triage context first and end CI log tails at the failure ([272097e](https://github.com/anthony-spruyt/agent-platform/commit/272097ebdae466813bdd4038addcc07020684bec))

## [1.9.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.8.0...v1.9.0) (2026-10-13)


### Features

* **triage:** add the precheck, check run and code rungs to Renovate triage ([9ebd523](https://github.com/anthony-spruyt/agent-platform/commit/9ebd5231afceac3ee5503c884fcbffd3fed5bc21))

## [1.8.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.7.0...v1.8.0) (2026-10-13)


### Features

* **triage:** run repository-scoped agents on the read tier ([#53](https://github.com/anthony-spruyt/agent-platform/issues/53)) ([cf3c7a5](https://github.com/anthony-spruyt/agent-platform/commit/cf3c7a5fe4d104add3eea958d45362d7d6a00359))

## [1.7.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.6.0...v1.7.0) (2026-10-13)


### Features

* **triage:** stop reading the old agent/needs-review label ([#48](https://github.com/anthony-spruyt/agent-platform/issues/48)) ([5ead30e](https://github.com/anthony-spruyt/agent-platform/commit/5ead30e11b1ebe4d1f3734de03699e7b75bc2d6b))

## [1.6.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.5.0...v1.6.0) (2026-10-13)


### Features

* **triage:** write agent/needs-human-review and read both names ([#44](https://github.com/anthony-spruyt/agent-platform/issues/44)) ([f446a3e](https://github.com/anthony-spruyt/agent-platform/commit/f446a3e3a4a6a676f838d21a34ee076fd2aa56c2))

## [1.5.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.4.1...v1.5.0) (2026-10-12)


### Features

* add RenovateTriage Live mode, GitHub writes and the bump-type guard ([#39](https://github.com/anthony-spruyt/agent-platform/issues/39)) ([761eec1](https://github.com/anthony-spruyt/agent-platform/commit/761eec12c8138833af56cc319de4ce7361dd269d))

## [1.4.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.4.0...v1.4.1) (2026-10-12)


### Bug Fixes

* record shadow verdicts despite the live path's output ([#35](https://github.com/anthony-spruyt/agent-platform/issues/35)) ([30b7e51](https://github.com/anthony-spruyt/agent-platform/commit/30b7e518039e1ea7cc4e65479b0389d04ba097c5))

## [1.4.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.3.0...v1.4.0) (2026-10-12)


### Features

* add the RenovateTriage workflow in shadow mode ([#30](https://github.com/anthony-spruyt/agent-platform/issues/30)) ([327b343](https://github.com/anthony-spruyt/agent-platform/commit/327b34371e16c0d945fad4ac992f9f31885a5b4e))

## [1.3.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.2.1...v1.3.0) (2026-10-12)


### Features

* add GitHub webhook intake and App client ([#21](https://github.com/anthony-spruyt/agent-platform/issues/21)) ([278f0bf](https://github.com/anthony-spruyt/agent-platform/commit/278f0bf8843bc1f179b44cb42ea904a18758e4a9))

## [1.2.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.2.0...v1.2.1) (2026-10-11)


### Bug Fixes

* return the worker's fault exit code from the host ([#20](https://github.com/anthony-spruyt/agent-platform/issues/20)) ([605fc33](https://github.com/anthony-spruyt/agent-platform/commit/605fc333ee828e8d37f44ee23c472b98eb286334))

## [1.2.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.1.0...v1.2.0) (2026-10-11)


### Features

* add run-data store and Migrate role ([#13](https://github.com/anthony-spruyt/agent-platform/issues/13)) ([6864e72](https://github.com/anthony-spruyt/agent-platform/commit/6864e72d04bb6feb440073fe396891d4962fae39))


### Bug Fixes

* keep workflows running when run-data recording fails ([#16](https://github.com/anthony-spruyt/agent-platform/issues/16)) ([f73a008](https://github.com/anthony-spruyt/agent-platform/commit/f73a008ce5087d8a08d8d0fc319ec19395d72c72))

## [1.1.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.0.0...v1.1.0) (2026-10-11)


### Features

* add RunAgentPod activity, AgentGateway and spike workflow ([#7](https://github.com/anthony-spruyt/agent-platform/issues/7)) ([6fb4a32](https://github.com/anthony-spruyt/agent-platform/commit/6fb4a3211dd8e2013e54d2f79696978d62bb1ee7))

## 1.0.0 (2026-10-10)


### Features

* scaffold .NET solution with Aspire, Temporal worker host and CI ([#2](https://github.com/anthony-spruyt/agent-platform/issues/2)) ([9e2ac72](https://github.com/anthony-spruyt/agent-platform/commit/9e2ac727c69ec59e12f9a6ca62851bbd9388bb13))
