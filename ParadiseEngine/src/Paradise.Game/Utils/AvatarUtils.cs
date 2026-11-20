using Paradise.Core.Avatars;
using Paradise.Core.Formats;
using Paradise.Core.Online;

namespace Paradise.Game.Utils;

/// <summary>
/// Helper functions for avatar shop data (clothes and hair shops): stock counts per spot and per clothing part.
/// "Owned" counts how many of the available items (unique clothing hashes) the player owns.
/// "Available" counts how many unique clothing items are available at that spot for the given avatar sex.
/// </summary>
public static class AvatarUtils
{
    /// <summary>Number of owned and available unique items.</summary>
    public readonly record struct ShopStockCount(int Owned, int Available);

    /// <summary>Stock counts for the extra clothing parts of a clothes shop (English names as requested).</summary>
    public sealed class ExtraPartsCount
    {
        /// <summary>Helmets / hats.</summary>
        public ShopStockCount Hats { get; init; }
        /// <summary>Tops / sweaters / pullovers (includes dresses where applicable).</summary>
        public ShopStockCount Pullovers { get; init; }
        /// <summary>Bottoms / pants.</summary>
        public ShopStockCount Pants { get; init; }
        /// <summary>Shoes.</summary>
        public ShopStockCount Shoes { get; init; }
        /// <summary>Glasses / sunglasses.</summary>
        public ShopStockCount Sunglasses { get; init; }
        /// <summary>Gloves.</summary>
        public ShopStockCount Gloves { get; init; }
    }

    // DBClothe._brand collections (same values as AvatarShop)
    const int BrandHighlife = 57, BrandStreetwear = 85, BrandSportswear = 84, BrandFashion = 83, BrandEden = 44;
    const int BrandCasino = 87, BrandPnj = 73, BrandDefault = 54, BrandHair = 55;

    /// <summary>
    /// Returns how many clothes are owned vs available at the given clothes shop spot (EAS_*).
    /// Returns (0, 0) if the spot is not a clothes shop or avatar data is not ready.
    /// </summary>
    public static ShopStockCount GetClothesCountFromSpot(Spot? spot, int sex = -1)
    {
        if (spot == null || !IsClothesSpot(spot) || !AvatarLibrary.Ready) return new ShopStockCount(0, 0);
        sex = ResolveSex(sex);
        var owned = OwnedClothesSet();
        int brand = PickClothesBrand(spot, sex, owned);
        var available = CollectClothesForSpot(spot, sex, null, brand);
        return CountOwned(available, owned);
    }

    /// <summary>
    /// Returns how many hair items (haircuts and beards) are owned vs available at the given hairdresser spot (EHD_*).
    /// Returns (0, 0) if the spot is not a hairdresser or avatar data is not ready.
    /// </summary>
    public static ShopStockCount GetHairCountFromSpot(Spot? spot, int sex = -1)
    {
        if (spot == null || !IsHairSpot(spot) || !AvatarLibrary.Ready) return new ShopStockCount(0, 0);
        sex = ResolveSex(sex);
        var owned = OwnedClothesSet();
        var available = CollectHairForSex(sex);
        return CountOwned(available, owned);
    }

    /// <summary>
    /// Returns owned/available counts for the extra clothing parts at the given clothes shop spot:
    /// Hats, Pullovers (tops), Pants (bottoms), Shoes, Sunglasses (glasses), Gloves.
    /// Returns zero counts if the spot is not a clothes shop or avatar data is not ready.
    /// </summary>
    public static ExtraPartsCount GetExtraPartsCountFromSpot(Spot? spot, int sex = -1)
    {
        if (spot == null || !IsClothesSpot(spot) || !AvatarLibrary.Ready)
        {
            var z = new ShopStockCount(0, 0);
            return new ExtraPartsCount { Hats = z, Pullovers = z, Pants = z, Shoes = z, Sunglasses = z, Gloves = z };
        }
        sex = ResolveSex(sex);
        var owned = OwnedClothesSet();
        int brand = PickClothesBrand(spot, sex, owned);
        return new ExtraPartsCount
        {
            Hats = CountOwned(CollectClothesForSpot(spot, sex, PartsForCategory("hats", sex), brand), owned),
            Pullovers = CountOwned(CollectClothesForSpot(spot, sex, PartsForCategory("tops", sex), brand), owned),
            Pants = CountOwned(CollectClothesForSpot(spot, sex, PartsForCategory("bottoms", sex), brand), owned),
            Shoes = CountOwned(CollectClothesForSpot(spot, sex, PartsForCategory("shoes", sex), brand), owned),
            Sunglasses = CountOwned(CollectClothesForSpot(spot, sex, PartsForCategory("glasses", sex), brand), owned),
            Gloves = CountOwned(CollectClothesForSpot(spot, sex, PartsForCategory("gloves", sex), brand), owned),
        };
    }

    /// <summary>True if the spot is a clothes shop (EAS_*).</summary>
    public static bool IsClothesSpot(Spot spot)
        => (spot.Code ?? "").StartsWith("EAS", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the spot is a hairdresser (EHD_*).</summary>
    public static bool IsHairSpot(Spot spot)
        => (spot.Code ?? "").StartsWith("EHD", StringComparison.OrdinalIgnoreCase);

    static int ResolveSex(int sex)
    {
        if (sex == 0 || sex == 1) return sex;
        return AvatarConfigs.Local.Sex;
    }

    static HashSet<int> OwnedClothesSet()
    {
        var s = new HashSet<int>();
        var list = TDU2Session.Save?.ownedClothes;
        if (list != null) foreach (int h in list) s.Add(h);
        return s;
    }

    static ShopStockCount CountOwned(HashSet<int> available, HashSet<int> owned)
    {
        int own = 0;
        foreach (int h in available) if (owned.Contains(h)) own++;
        return new ShopStockCount(own, available.Count);
    }

    /// <summary>Parts for a named clothing category (hats, glasses, tops, bottoms, shoes, gloves), matching shop cameras.</summary>
    static ClothePart[]? PartsForCategory(string category, int sex) => (category ?? "").ToLowerInvariant() switch
    {
        "hats" => new[] { ClothePart.Helmet },
        "glasses" => new[] { ClothePart.Glasses },
        "tops" => sex == 1 ? new[] { ClothePart.Top, ClothePart.Dress } : new[] { ClothePart.Top },
        "bottoms" => new[] { ClothePart.Bottom },
        "shoes" => new[] { ClothePart.Shoes },
        "gloves" => new[] { ClothePart.Gloves },
        _ => null,
    };

    /// <summary>All clothing shop part groups (same categories as in GAME_MODE_GOODIES).</summary>
    static readonly string[] ClothesCategories = { "hats", "glasses", "tops", "bottoms", "shoes", "gloves" };
    /// <summary>Clothes brands sold in shops (excludes PNJ/Default/Hair/Casino).</summary>
    static readonly int[] ClothesBrands = { BrandHighlife, BrandStreetwear, BrandSportswear, BrandFashion, BrandEden };

    /// <summary>
    /// Collects unique available clothing hashes for a clothes shop spot.
    /// If <paramref name="parts"/> is null, collects across all shop categories; otherwise only for the given parts.
    /// </summary>
    static HashSet<int> CollectClothesForSpot(Spot spot, int sex, ClothePart[]? parts, int brand)
    {
        if (parts != null)
            return CollectClothesForParts(parts, sex, brand);

        var all = new HashSet<int>();
        foreach (var cat in ClothesCategories)
        {
            var p = PartsForCategory(cat, sex);
            if (p == null) continue;
            foreach (int h in CollectClothesForParts(p, sex, brand)) all.Add(h);
        }
        return all;
    }

    /// <summary>
    /// Picks the most plausible clothes brand for a shop spot.
    /// The real shop prefers the seller's brand over the spot category (GOODIES.BrandFromSeller), but the seller is not
    /// known on the map. We therefore choose the brand whose total available assortment contains the most items the player
    /// already owns (tie-break: spot-derived brand, then larger assortment). This makes the owned/available count reflect
    /// the shop the player likely bought from.
    /// </summary>
    static int PickClothesBrand(Spot spot, int sex, HashSet<int> owned)
    {
        int spotBrand = BrandForSpot(spot);
        int bestBrand = spotBrand;
        int bestOwned = -1, bestAvail = -1;
        bool bestIsSpot = false;

        foreach (int brand in ClothesBrands)
        {
            int avail = 0, own = 0;
            foreach (var cat in ClothesCategories)
            {
                var p = PartsForCategory(cat, sex);
                if (p == null) continue;
                CountBrandForParts(p, sex, brand, owned, ref avail, ref own);
            }
            if (avail == 0) continue;

            bool isSpot = brand == spotBrand;
            bool better = own > bestOwned;
            if (!better && own == bestOwned)
                better = (isSpot && !bestIsSpot) || (isSpot == bestIsSpot && avail > bestAvail);
            if (better)
            {
                bestBrand = brand;
                bestOwned = own;
                bestAvail = avail;
                bestIsSpot = isSpot;
            }
        }

        // Unknown spot category and no brand yielded matches: fall back to "any sellable brand" behavior (brand 0).
        if (bestAvail < 0) return 0;
        return bestBrand;
    }

    static void CountBrandForParts(ClothePart[] parts, int sex, int brand, HashSet<int> owned, ref int avail, ref int own)
    {
        foreach (var c in AvatarLibrary.AllClothes)
        {
            if (Array.IndexOf(parts, c.Part) < 0 || !Sells(c, sex, brand, false)) continue;
            avail++;
            if (owned.Contains(c.Hash)) own++;
        }
    }

    static HashSet<int> CollectClothesForParts(ClothePart[] parts, int sex, int brand)
    {
        var result = new HashSet<int>();
        // Mirror GAME_MODE_GOODIES.BuildItems: for a specific brand list that brand's items;
        // if the spot has no known brand, fall back to all sellable clothes brands.
        if (brand != 0)
        {
            foreach (var c in AvatarLibrary.AllClothes)
                if (Array.IndexOf(parts, c.Part) >= 0 && Sells(c, sex, brand, false)) result.Add(c.Hash);
            if (result.Count > 0) return result;
        }
        foreach (var c in AvatarLibrary.AllClothes)
            if (Array.IndexOf(parts, c.Part) >= 0 && Sells(c, sex, 0, true)) result.Add(c.Hash);
        return result;
    }

    /// <summary>Whether a clothes shop sells this item (mirrors GAME_MODE_GOODIES.Sells).</summary>
    static bool Sells(ClotheInfo c, int sex, int brand, bool anyBrand)
    {
        if (!c.Valid || c.IsNothing) return false;
        if (sex == 1 ? !c.ForWoman : !c.ForMan) return false;
        if (!AvatarLibrary.HasBank(c)) return false;
        if (c.Brand == BrandPnj || c.Brand == BrandDefault || c.Brand == BrandHair || c.Brand == BrandCasino) return false;
        return anyBrand || c.Brand == brand;
    }

    /// <summary>Clothes collection brand for a shop spot (from spots.bin TypeA high byte, as in GAME_MODE_GOODIES.BrandFor).</summary>
    static int BrandForSpot(Spot spot)
    {
        int cat = ((spot.TypeA) >> 8) & 0xFF;
        return cat switch
        {
            15 => BrandHighlife,
            16 => BrandStreetwear,
            17 => BrandSportswear,
            18 => BrandFashion,
            19 => BrandEden,
            _ => 0,
        };
    }

    /// <summary>
    /// Collects unique available hair/beard item hashes for a hairdresser (same criteria as GAME_MODE_HAIRDRESSER).
    /// </summary>
    static HashSet<int> CollectHairForSex(int sex)
    {
        var result = new HashSet<int>();
        bool woman = sex == 1;
        foreach (var c in AvatarLibrary.AllClothes)
        {
            if (c.Part != ClothePart.Hair && c.Part != ClothePart.Beard) continue;
            if (!c.Valid || c.Brand == BrandPnj) continue;
            if (woman ? !c.ForWoman : !c.ForMan) continue;
            if (c.File.EndsWith("_HELMET", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.IsNothing) continue; // "shave"/no-beard placeholder is not a purchasable item
            if (!AvatarLibrary.HasBank(c)) continue;
            // hairdresser only lists cuts with a known haircut style (or beard styles); keep items that have a style or are regular cuts
            // (mirrors BuildLists: unknown non-zero styles for hair fall into short; beards need known styles except nothing handled above)
            result.Add(c.Hash);
        }
        return result;
    }
}
