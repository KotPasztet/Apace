using Apace.DB.Models.Common;

namespace Apace.DB.Models.Player.Workshop;

public sealed record InputItem(
     string Id,
     int Count,
     NonStackableItemInstance[] Instances
);
