# MODIFY integrated registry-fixture regression, 2026-10-20

## Boundary and cause

Base: `885f617042af0e27878c67ab68d8acf640d2e0d9`. This slice changes only the new test fixture and this record. It does not change production registry discovery, Registry, AutoSync, lifecycle algorithms, assertions, or timeouts.

The coordinator's preserved `regression01/closing-gi/closing-gi.trx` has 3353 passed and 3 failed tests (3356 total). This is a new regression introduced by `AutoRegistryApplyCompletionTests`, not a historical baseline failure. Original TRX SHA256: `29479a2fd2609cb760bccc3d9c03e22be91398654b66c0fd04ad002ab29f0e00`.

`RegistryModule.GetRegistries` scans `AppDomain.CurrentDomain.GetDomainTypes()` and accepts `IsConcrete` implementations of `IAutoRegistry<>`. `GetDomainTypes` enumerates all loaded assemblies, including Castle's dynamic assembly. `InterfaceCollector.IsConcrete` excludes generic or abstract classes but accepts the non-generic generated ``Castle.Proxies.AutoRegistryBase`1Proxy``. The receipt tests previously generated that proxy with `Mock<AutoRegistryBase<Instance>>`.

The next actual `PatchBootstrap` build auto-activated the proxy without its Moq interceptor. Calling the abstract `Constructors` getter threw `NotImplementedException`. `GovernorKingdomCreationPatchesTests.ClientFinalization_PublishesKingdomCreationRequestAndSkipsOriginal` failed in this constructor path; because construction had acquired the bootstrap semaphore and did not return a disposable instance, both `EmptyKingdomName_DoesNotCreateClientRequest` rows subsequently failed with the original 10-second semaphore error. These secondary errors are consequences, not a reason to extend the timeout.

## Fixture adaptation and invariants

A nested sealed `ReceiptRegistry<T>` implements the existing registry contract directly, preserving callback counts, callback exceptions, create rejection, missing destroy identity, and removal behavior. Reflection sees its generic definition, which the original discovery rule excludes; no Castle registry subclass is emitted. Interface mocks remain unchanged.

The original six theory rows still check final-barrier success/failure. The new same-process regression invokes all six before an actual `PatchBootstrap` build, resolves the real `IObjectManager`, and confirms the container is installed. It asserts both the reflected generic definition and the closed fixture are excluded by the existing `IsConcrete` rule. The test class uses the existing non-parallel `ModInformationRoleCollection` used by the actual Governor container tests and restores `ContainerProvider` in `finally`.

No production core modification is required: the fixture adapts to established discovery semantics. The protected production registration, publication, object IDs, and message apply completion behavior remain unchanged.

## Verification

Final source: full GameInterface **3357 passed / 0 failed / 0 skipped**, focused fixture **7 passed / 0 failed / 0 skipped**, both exit 0. Exact-name comparison against the original 3356 tests removes no test; the sole added test is the same-process registry-discovery regression. The original three failing Governor rows now pass. Raw TRX, command logs, SHA256 and `comparison.json` are in `artifacts/modify-registry-fixture-m2`; original failures are retained by the coordinator. A first local compile attempt used a nonexistent test-only `ContainerProvider.GetContainer` accessor; it was corrected to the verified `TryGetContainer` API, and its raw failed build log is retained in `artifacts/modify-registry-fixture-m2/attempt01-compile.log`.

This worker does not deploy or run the game. This regression fix supplies no new dual-process or authority-migration runtime PASS.

Full GI command: `dotnet test source/GameInterface.Tests/GameInterface.Tests.csproj -p:ModName= --no-restore --logger "trx;LogFileName=registry-fixture-final-gi.trx" --results-directory artifacts/modify-registry-fixture-m2/final-gi`.

Focused command: `dotnet test source/GameInterface.Tests/GameInterface.Tests.csproj -p:ModName= --no-build --no-restore --filter FullyQualifiedName~AutoRegistryApplyCompletionTests --logger "trx;LogFileName=registry-fixture-final-focused.trx" --results-directory artifacts/modify-registry-fixture-m2/final-focused`.
