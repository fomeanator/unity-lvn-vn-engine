using System.Runtime.CompilerServices;

// VnStage exposes internal static helpers (PlacementFrom, ParseColor,
// ParseTransition, AxesFrom, ReservedActorFields) that the EditMode tests drive
// directly without spinning up a UIDocument.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
