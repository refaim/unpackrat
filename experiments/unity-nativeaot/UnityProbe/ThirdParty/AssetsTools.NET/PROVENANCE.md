# AssetsTools.NET iterator adaptation

Origin: AssetsTools.NET by nesrak1, MIT (LICENSE alongside this file).
The complete iterator source is pinned to upstream commit
`471bad9a9f24c14db4900ce63e30d24d5a4b60ef`, Git blob
`04906b950c62484bf2400ed0f3d678d56c3154b5`:
https://github.com/nesrak1/AssetsTools.NET/blob/471bad9a9f24c14db4900ce63e30d24d5a4b60ef/AssetTools.NET/Standard/AssetTypeClass/AssetTypeValueIterator.cs

The package remains AssetsTools.NET 3.0.5. Its netstandard2.0 DLL SHA256 is
`6B03D589163623B67620FBBC6F425DCB47F856584BD3AB1A34C896B2666372E4`.
The original package iterator is the differential test oracle; complete source
and package binary equivalence is not asserted.

Local changes rename the class/namespace, make it internal, and replace boxed
child-list enumerators and array yield iterators with per-depth reusable frames.
Ordinary frames retain the real unboxed List enumerator, including version checks.
Array frames retain the upstream yield Current behavior. Retired frames clear
schema references, and reset clears only active frames. Scratch is instance owned;
there is no cross-open/global pool or reuse of retained projection values.
All upstream traversal/value/alignment APIs remain, including existing error
behavior. In particular, the managed-registry skip does not acquire the later
upstream TemplateField assignment fix. Projection continues rejecting registry
fields before attempting that skip.

The adaptation also exposes the count already decoded by ReadNext for its current
ordinary array. Projection consumes that count immediately after successful ReadNext
instead of rereading the same prefix; byte-array traversal, materialization, reset,
error order, and mature endian decoding remain unchanged. The count has no contract
for other fields or before a successful ordinary-array ReadNext.

The complete compiled adaptation belongs to the existing probe assembly and its
100% line/branch coverage gate; it is not excluded as vendored code. License and
this receipt must stay with the adaptation. No independent repository/build or
package replacement is introduced.
