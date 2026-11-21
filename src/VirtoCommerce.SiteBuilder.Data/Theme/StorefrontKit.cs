namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <param name="HasCatalog">A catalog was given: without one, nothing is known of the kit.</param>
/// <param name="Tags">The attributes of each kit tag, by its name; <c>null</c> for a catalog without a kit.</param>
/// <param name="Behaviors">Each behavior by its name; <c>null</c> for a catalog without a kit.</param>
/// <param name="Icons">The storefront's icon names; <c>null</c> when the kit lists none, and an icon name is not checked.</param>
public sealed record StorefrontKit(bool HasCatalog, IReadOnlyDictionary<string, IReadOnlyDictionary<string, KitField>> Tags,
    IReadOnlyDictionary<string, KitBehavior> Behaviors, IReadOnlySet<string> Icons);

/// <param name="Type">string, number, boolean, select, date or link; any other refuses the kit tag or the behavior that declares it.</param>
/// <param name="OptionsFrom">Where a select's values are listed instead of its options: "icons" for the kit's icon names.</param>
public sealed record KitField(string Type, IReadOnlyList<string> Options, decimal? Min, decimal? Max, string OptionsFrom = null);

/// <param name="Value">The type of the behavior attribute's own value; <c>null</c> when it declares none.</param>
/// <param name="RepeatsChildren">The behavior copies what its element holds, as a marquee does, so it may not stand inside another that does.</param>
public sealed record KitBehavior(KitField Value, IReadOnlyDictionary<string, KitField> Options, bool RepeatsChildren = false);
