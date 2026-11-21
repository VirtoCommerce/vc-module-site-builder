namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <param name="HasCatalog">A catalog was given: without one, nothing is known of the kit.</param>
/// <param name="Tags">The attributes of each kit tag, by its name; <c>null</c> for a catalog without a kit.</param>
/// <param name="Behaviors">Each behavior by its name; <c>null</c> for a catalog without a kit.</param>
public sealed record StorefrontKit(bool HasCatalog, IReadOnlyDictionary<string, IReadOnlyDictionary<string, KitField>> Tags,
    IReadOnlyDictionary<string, KitBehavior> Behaviors);

/// <param name="Type">string, number, boolean, select, date or link; any other refuses the kit tag or the behavior that declares it.</param>
public sealed record KitField(string Type, IReadOnlyList<string> Options, decimal? Min, decimal? Max);

/// <param name="Value">The type of the behavior attribute's own value; <c>null</c> when it declares none.</param>
public sealed record KitBehavior(KitField Value, IReadOnlyDictionary<string, KitField> Options);
