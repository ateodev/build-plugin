namespace Ateo.Build
{
	/// <summary>
	/// Author intent for WHERE a <see cref="PostBuildAction"/> instance should run (per-instance, distinct from
	/// the hard capability gate). <see cref="Local"/> = the dev's in-process Build Panel build only (e.g.
	/// <c>AdbInstall</c>); <see cref="Remote"/> = the headless CI server only (e.g. a Steam publish so local
	/// test builds never ship); <see cref="Both"/> = run wherever applicable. When this and the capability gate
	/// conflict, capability wins (see build-plugin-architecture.md §10).
	/// </summary>
	public enum RunLocation
	{
		Local,
		Remote,
		Both
	}
}
