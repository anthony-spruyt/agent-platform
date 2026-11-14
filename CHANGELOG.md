# Changelog

## [1.27.1](https://github.com/anthony-spruyt/agent-platform/compare/v1.27.0...v1.27.1) (2026-11-14)


### Bug Fixes

* **deps:** update dependency modelcontextprotocol.aspnetcore to 2.4.0 ([#309](https://github.com/anthony-spruyt/agent-platform/issues/309)) ([98c677d](https://github.com/anthony-spruyt/agent-platform/commit/98c677d68ef8dea37e0caab0808b06d1c0201a0a))
* **deps:** update dotnet monorepo ([#302](https://github.com/anthony-spruyt/agent-platform/issues/302)) ([f2bfba6](https://github.com/anthony-spruyt/agent-platform/commit/f2bfba6c5d215249cd70d7b55fb343c707a40fda))
* **deps:** update opentelemetry-dotnet-contrib monorepo to 1.20.2 ([#301](https://github.com/anthony-spruyt/agent-platform/issues/301)) ([0b58997](https://github.com/anthony-spruyt/agent-platform/commit/0b58997aaec9fe87fb24f3322587568a3620468c))

## [1.27.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.26.0...v1.27.0) (2026-11-14)


### Features

* **triage:** wait for the CI summary check and follow CI after an approval ([#306](https://github.com/anthony-spruyt/agent-platform/issues/306)) ([6b3bad3](https://github.com/anthony-spruyt/agent-platform/commit/6b3bad3a228528429174d4cca5cb21e0de49d5f2))


### Bug Fixes

* **deps:** update aspire monorepo to 13.6.5 ([#307](https://github.com/anthony-spruyt/agent-platform/issues/307)) ([1c6f97c](https://github.com/anthony-spruyt/agent-platform/commit/1c6f97cc7054eb5aaba3a354632b54689b102662))

## [1.26.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.25.0...v1.26.0) (2026-11-11)


### Features

* **livemap:** patch a hard build with an agent ([#299](https://github.com/anthony-spruyt/agent-platform/issues/299)) ([13f7c39](https://github.com/anthony-spruyt/agent-platform/commit/13f7c3988500e44aa0cb1bda725a63b83c4f0c92))


### Bug Fixes

* **deps:** update dependency modelcontextprotocol.aspnetcore to 2.3.1 ([#296](https://github.com/anthony-spruyt/agent-platform/issues/296)) ([3fb8c6f](https://github.com/anthony-spruyt/agent-platform/commit/3fb8c6f0cf6b476a97f755f70c51d0344dd885b9))

## [1.25.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.24.0...v1.25.0) (2026-11-09)


### Features

* **livemap:** merge the release, check it and pin it in spruyt-labs ([#286](https://github.com/anthony-spruyt/agent-platform/issues/286)) ([92ada62](https://github.com/anthony-spruyt/agent-platform/commit/92ada6265a22196415ccfe85f984af64c789638a))


### Bug Fixes

* **livemap:** accept a release pull request made on an older commit of main ([#289](https://github.com/anthony-spruyt/agent-platform/issues/289)) ([7925682](https://github.com/anthony-spruyt/agent-platform/commit/792568220b7d3fb2fefd3f9b9fe741bb51420608))

## [1.24.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.23.0...v1.24.0) (2026-11-09)


### Features

* **livemap:** open the data pull request and merge it once the checks pass ([#282](https://github.com/anthony-spruyt/agent-platform/issues/282)) ([7eabfda](https://github.com/anthony-spruyt/agent-platform/commit/7eabfda80976bf3e90a4cc651f05f3badfceb4c6))

## [1.23.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.22.0...v1.23.0) (2026-11-09)


### Features

* **livemap:** check each detected build with the fork's workflows ([#278](https://github.com/anthony-spruyt/agent-platform/issues/278)) ([d6d9f3c](https://github.com/anthony-spruyt/agent-platform/commit/d6d9f3c2329c43c914ec8bd0aa1e528b01bcdfc7))

## [1.22.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.21.0...v1.22.0) (2026-11-08)


### Features

* **livemap:** detect new Enshrouded server builds ([#274](https://github.com/anthony-spruyt/agent-platform/issues/274)) ([b15471a](https://github.com/anthony-spruyt/agent-platform/commit/b15471ad1569db203d51d7f2f2fd67c0481aa82a))

## [1.21.0](https://github.com/anthony-spruyt/agent-platform/compare/v1.20.5...v1.21.0) (2026-11-08)


### Features

* **agent-pods:** add optional pull request and run overrides to agent runs ([#256](https://github.com/anthony-spruyt/agent-platform/issues/256)) ([792de07](https://github.com/anthony-spruyt/agent-platform/commit/792de077ec7c6348f0c767138b86633be58f95f9))


### Bug Fixes

* **deps:** update temporalio-dotnet to 1.21.1 ([#265](https://github.com/anthony-spruyt/agent-platform/issues/265)) ([d2e5180](https://github.com/anthony-spruyt/agent-platform/commit/d2e51800cd4bec5e4213392165e77609495be0ec))

## [1.20.5](https://github.com/anthony-spruyt/agent-platform/compare/v1.20.4...v1.20.5) (2026-11-05)


### Bug Fixes

* **triage:** stop flagging lock refreshes, kubectl pins and digest pins ([#252](https://github.com/anthony-spruyt/agent-platform/issues/252)) ([d07507c](https://github.com/anthony-spruyt/agent-platform/commit/d07507cb5a53a7b91e081c91d47e7e96bd9f8126))

## [1.20.4](https://github.com/anthony-spruyt/agent-platform/compare/v1.20.3...v1.20.4) (2026-11-04)


### Bug Fixes

* **deps:** update .NET base images to 10.0.14 ([#244](https://github.com/anthony-spruyt/agent-platform/issues/244)) ([2defcd1](https://github.com/anthony-spruyt/agent-platform/commit/2defcd181616800ae5633566f5baee4f576a2d2c))
* **deps:** update opentelemetry-dotnet-contrib monorepo to 1.20.1 ([#212](https://github.com/anthony-spruyt/agent-platform/issues/212)) ([8ffd835](https://github.com/anthony-spruyt/agent-platform/commit/8ffd83515d98ddc37d6932e6261871b529f31e44))

## [1.20.3](https://github.com/anthony-spruyt/agent-platform/compare/v1.20.2...v1.20.3) (2026-11-04)


### Bug Fixes

* **deps:** update aspire monorepo to 13.6.3 ([#215](https://github.com/anthony-spruyt/agent-platform/issues/215)) ([12dcd45](https://github.com/anthony-spruyt/agent-platform/commit/12dcd45d59382d6a10246efa71cdc3e4dc5e0372))
* **deps:** update aspire monorepo to 13.6.4 ([#222](https://github.com/anthony-spruyt/agent-platform/issues/222)) ([c68e4b3](https://github.com/anthony-spruyt/agent-platform/commit/c68e4b388b5074dad9e123b4f4ae766c3f8e6516))
* **deps:** update dependency modelcontextprotocol.aspnetcore to 2.3.0 ([#180](https://github.com/anthony-spruyt/agent-platform/issues/180)) ([abfe82b](https://github.com/anthony-spruyt/agent-platform/commit/abfe82b60a5a464d19b33981395ee476db16a475))
* **deps:** update dotnet monorepo to 10.0.14 ([#226](https://github.com/anthony-spruyt/agent-platform/issues/226)) ([ef9942d](https://github.com/anthony-spruyt/agent-platform/commit/ef9942d5b5511690081e3661eff40c9d98f9b0e9))


### Code Refactoring

* **claude-budget:** share the Claude budget across workflow kinds ([#239](https://github.com/anthony-spruyt/agent-platform/issues/239)) ([81dbb1e](https://github.com/anthony-spruyt/agent-platform/commit/81dbb1e03bfd16eec68226017a9a6009c0a2149d))
* **webhook:** route GitHub events through shared handlers ([#237](https://github.com/anthony-spruyt/agent-platform/issues/237)) ([4583435](https://github.com/anthony-spruyt/agent-platform/commit/4583435e625c1c382341251df1fade22b1246849))

## [1.20.2](https://github.com/anthony-spruyt/agent-platform/compare/v1.20.1...v1.20.2) (2026-10-28)


### Bug Fixes

* **triage:** re-check CI on a timer while waiting ([#205](https://github.com/anthony-spruyt/agent-platform/issues/205)) ([2308c12](https://github.com/anthony-spruyt/agent-platform/commit/2308c12473663504169eb4c91cae9936cd120934))

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
