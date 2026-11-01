namespace Skyrift.Data
{
    /// <summary>
    /// Pickup kinds (TECH_DOC §5.6). Lives in Data because it is a content field on
    /// <c>CollectibleDefinition</c> and a key into the scoring weight table — both of which are data.
    /// Award routing for a new kind is an <c>ICollectibleKindBehaviour</c> in Gameplay (M2), never a
    /// change to this enum's consumers.
    /// </summary>
    public enum CollectibleKind
    {
        Coin = 0,
        Key,
        Gem,
        Letter,
        PowerUpPickup,
        MysteryBox
    }
}