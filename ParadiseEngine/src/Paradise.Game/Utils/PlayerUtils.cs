using Paradise.Core.Avatars;
using Paradise.Core.Online;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Paradise.Game.Utils;

/// <summary>
/// Helper functions for player and general savegame data (profile/session, money, world position,
/// bookmarks, favorites, avatar, story/progress, radio, options and news). Operates on the current
/// session save by default; most methods also accept an explicit <see cref="SaveData"/>.
/// </summary>
public static class PlayerUtils
{
    static SaveData? Save(SaveData? save) => save ?? TDU2Session.Save;

    // --------------------------------------------------------------------- Session / profile

    /// <summary>Nickname of the current session, or null if none.</summary>
    public static string? Nick => TDU2Session.Nick;

    /// <summary>True if a player is logged in (online or offline session with a save).</summary>
    public static bool IsLoggedIn => TDU2Session.LoggedIn;

    /// <summary>True if the current session is an offline profile.</summary>
    public static bool IsOffline => TDU2Session.Offline;

    /// <summary>Current server/session id, or null if none.</summary>
    public static string? SessionId => TDU2Session.SessionId;

    /// <summary>Number of online players reported by the session, or 0.</summary>
    public static int OnlinePlayers => TDU2Session.Online;

    // --------------------------------------------------------------------- Money

    /// <summary>Current money amount (clamped to >= 0 when read via helpers).</summary>
    public static long GetMoney(SaveData? save = null) => Save(save)?.money ?? 0;

    /// <summary>Sets the money amount (clamped to >= 0). Returns false if there is no save.</summary>
    public static bool SetMoney(long amount, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.money = Math.Max(0, amount);
        return true;
    }

    /// <summary>Adds (or subtracts if negative) money, clamped to >= 0. Returns the new amount, or 0 if no save.</summary>
    public static long AddMoney(long amount, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return 0;
        s.money = Math.Max(0, s.money + amount);
        return s.money;
    }

    /// <summary>True if the player can afford the given amount.</summary>
    public static bool CanAfford(long amount, SaveData? save = null) => amount <= 0 || GetMoney(save) >= amount;

    /// <summary>Spends money if affordable. Returns true if the amount was deducted.</summary>
    public static bool SpendMoney(long amount, SaveData? save = null)
    {
        if (amount < 0) return false;
        var s = Save(save);
        if (s == null || s.money < amount) return false;
        s.money -= amount;
        return true;
    }

    /// <summary>True if a world position is stored in the save.</summary>
    public static bool HasPosition(SaveData? save = null) => Save(save)?.hasPosition == true;

    /// <summary>Gets the stored world position and heading (degrees). HasPosition is false if none stored.</summary>
    public static (float X, float Y, float Z, float Heading, bool HasPosition) GetPosition(SaveData? save = null)
    {
        var s = Save(save);
        return s == null ? default : (s.px, s.py, s.pz, s.heading, s.hasPosition);
    }

    /// <summary>Sets the stored world position and heading (degrees). Returns false if there is no save.</summary>
    public static bool SetPosition(float x, float y, float z, float headingDegrees, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.hasPosition = true;
        s.px = x; s.py = y; s.pz = z; s.heading = headingDegrees;
        return true;
    }

    /// <summary>Clears the stored world position flag. Returns false if there is no save.</summary>
    public static bool ClearPosition(SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.hasPosition = false;
        return true;
    }

    /// <summary>Current island key/name in the save (e.g. "ibiza", "hawai"), or empty string.</summary>
    public static string GetIsland(SaveData? save = null) => Save(save)?.island ?? "";

    /// <summary>Sets the current island key/name. Returns false if there is no save.</summary>
    public static bool SetIsland(string? island, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.island = island ?? "";
        return true;
    }

    /// <summary>Current interior location string ("SPOT|bank" or "SPOT||house"), empty if outside.</summary>
    public static string GetInterior(SaveData? save = null) => Save(save)?.interior ?? "";

    /// <summary>Sets the current interior location string (empty = outside). Returns false if there is no save.</summary>
    public static bool SetInterior(string? interior, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.interior = interior ?? "";
        return true;
    }

    /// <summary>True if the player is marked as being inside an interior.</summary>
    public static bool IsInside(SaveData? save = null) => !string.IsNullOrEmpty(GetInterior(save));

    /// <summary>Saved map bookmarks.</summary>
    public static IReadOnlyList<BookmarkEntry> GetBookmarks(SaveData? save = null)
        => (IReadOnlyList<BookmarkEntry>?)Save(save)?.bookmarks ?? Array.Empty<BookmarkEntry>();

    /// <summary>Number of saved map bookmarks.</summary>
    public static int GetBookmarkCount(SaveData? save = null) => Save(save)?.bookmarks?.Count ?? 0;

    /// <summary>Finds a bookmark by id, or null.</summary>
    public static BookmarkEntry? GetBookmark(int id, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.bookmarks == null) return null;
        foreach (var b in s.bookmarks)
            if (b != null && b.id == id) return b;
        return null;
    }

    /// <summary>Adds a bookmark at the given position and returns it, or null if there is no save.</summary>
    public static BookmarkEntry? AddBookmark(string island, float x, float y, float z, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return null;
        s.bookmarks ??= new List<BookmarkEntry>();
        int nextId = 1;
        foreach (var b in s.bookmarks)
            if (b != null && b.id >= nextId) nextId = b.id + 1;
        var entry = new BookmarkEntry { id = nextId, island = island ?? "", x = x, y = y, z = z };
        s.bookmarks.Add(entry);
        return entry;
    }

    /// <summary>Removes a bookmark by id. Returns true if removed.</summary>
    public static bool RemoveBookmark(int id, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.bookmarks == null) return false;
        return s.bookmarks.RemoveAll(b => b != null && b.id == id) > 0;
    }

    /// <summary>Removes all bookmarks. Returns true if any were removed.</summary>
    public static bool ClearBookmarks(SaveData? save = null)
    {
        var s = Save(save);
        if (s?.bookmarks == null || s.bookmarks.Count == 0) return false;
        s.bookmarks.Clear();
        return true;
    }

    /// <summary>Favorite spot codes.</summary>
    public static IReadOnlyList<string> GetFavoriteSpots(SaveData? save = null)
        => (IReadOnlyList<string>?)Save(save)?.favoriteSpots ?? Array.Empty<string>();

    /// <summary>True if the spot code is marked as favorite.</summary>
    public static bool IsFavoriteSpot(string? spotCode, SaveData? save = null)
    {
        var list = Save(save)?.favoriteSpots;
        if (list == null || string.IsNullOrEmpty(spotCode)) return false;
        foreach (var c in list)
            if (string.Equals(c, spotCode, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Adds a spot code to favorites if not present. Returns true if added.</summary>
    public static bool AddFavoriteSpot(string? spotCode, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null || string.IsNullOrEmpty(spotCode)) return false;
        s.favoriteSpots ??= new List<string>();
        if (IsFavoriteSpot(spotCode, s)) return false;
        s.favoriteSpots.Add(spotCode);
        return true;
    }

    /// <summary>Removes a spot code from favorites. Returns true if removed.</summary>
    public static bool RemoveFavoriteSpot(string? spotCode, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.favoriteSpots == null || string.IsNullOrEmpty(spotCode)) return false;
        return s.favoriteSpots.RemoveAll(c => string.Equals(c, spotCode, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Toggles a spot as favorite. Returns the new state (true = favorite).</summary>
    public static bool ToggleFavoriteSpot(string? spotCode, SaveData? save = null)
    {
        if (IsFavoriteSpot(spotCode, save)) { RemoveFavoriteSpot(spotCode, save); return false; }
        AddFavoriteSpot(spotCode, save);
        return true;
    }

    /// <summary>Encoded avatar configuration string from the save, or empty string.</summary>
    public static string GetAvatarConfig(SaveData? save = null) => Save(save)?.avatar ?? "";

    /// <summary>Sets the encoded avatar configuration string. Returns false if there is no save.</summary>
    public static bool SetAvatarConfig(string? avatar, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.avatar = avatar ?? "";
        return true;
    }

    /// <summary>
    /// Decoded avatar configuration from the save, or null if missing/invalid.
    /// For the active session (including offline fallback), see <see cref="GetLocalAvatar"/>.
    /// </summary>
    public static AvatarConfig? GetAvatar(SaveData? save = null)
        => AvatarConfig.Decode(GetAvatarConfig(save));

    /// <summary>Decoded avatar configuration from the save, or a default appearance if missing/invalid.</summary>
    public static AvatarConfig GetAvatarOrDefault(SaveData? save = null, int defaultSex = 0)
        => GetAvatar(save) ?? AvatarConfig.Default(defaultSex);

    /// <summary>
    /// The local/session avatar (decoded from the active session save or offline cache; falls back to default).
    /// </summary>
    public static AvatarConfig GetLocalAvatar() => AvatarConfigs.Local;

    /// <summary>Sets the avatar for the save (encodes the config). Returns false if config or save is null.</summary>
    public static bool SetAvatar(AvatarConfig? config, SaveData? save = null)
    {
        if (config == null) return false;
        if (save == null || ReferenceEquals(save, TDU2Session.Save))
        {
            AvatarConfigs.SetLocal(config);
            return true;
        }
        save.avatar = config.Encode();
        return true;
    }

    /// <summary>Avatar sex from the save: 0 = male, 1 = female. Returns -1 if no avatar is stored.</summary>
    public static int GetAvatarSex(SaveData? save = null) => GetAvatar(save)?.Sex ?? -1;

    /// <summary>Avatar sex for the active session (falls back to the local/default avatar).</summary>
    public static int GetLocalAvatarSex() => AvatarConfigs.Local.Sex;

    /// <summary>Display name for an avatar sex value: "Male", "Female", or "Unknown".</summary>
    public static string GetAvatarSexName(int sex) => sex switch { 0 => "Male", 1 => "Female", _ => "Unknown" };

    /// <summary>Avatar origin/ethnicity from the save: 0 = Occidental, 1 = Asian, 2 = African. Returns -1 if no avatar is stored.</summary>
    public static int GetAvatarEthnic(SaveData? save = null) => GetAvatar(save)?.Ethnic ?? -1;

    /// <summary>Display name for an avatar ethnicity/origin value.</summary>
    public static string GetAvatarEthnicName(int ethnic) => ethnic switch
    {
        0 => "Occidental",
        1 => "Asian",
        2 => "African",
        _ => "Unknown"
    };

    /// <summary>Avatar eye id from the save (-1 if not set or no avatar).</summary>
    public static int GetAvatarEyeId(SaveData? save = null) => GetAvatar(save)?.EyeId ?? -1;

    /// <summary>Avatar eyebrow id from the save (-1 if not set or no avatar).</summary>
    public static int GetAvatarBrowId(SaveData? save = null) => GetAvatar(save)?.BrowId ?? -1;

    /// <summary>Avatar eyebrow color from the save (alpha &lt; 0 if not set). Returns default if no avatar.</summary>
    public static Vector4 GetAvatarBrowColor(SaveData? save = null) => GetAvatar(save)?.BrowColor ?? new Vector4(0f, 0f, 0f, -1f);

    /// <summary>True if a custom eyebrow color is set on the saved avatar.</summary>
    public static bool HasAvatarBrowColor(SaveData? save = null) => GetAvatar(save)?.HasBrowColor == true;

    /// <summary>Number of worn clothing items encoded in the avatar appearance.</summary>
    public static int GetAvatarItemCount(SaveData? save = null) => GetAvatar(save)?.Items?.Count ?? 0;

    /// <summary>Worn clothing info for a part from the saved avatar, or null if none/unknown.</summary>
    public static ClotheInfo? GetWornItem(ClothePart part, SaveData? save = null) => GetAvatar(save)?.Worn(part);

    /// <summary>Hash of the worn clothing item for a part from the saved avatar, or 0 if none.</summary>
    public static int GetWornItemHash(ClothePart part, SaveData? save = null) => GetAvatar(save)?.Worn(part)?.Hash ?? 0;

    /// <summary>Selected color index for a worn clothing item hash from the saved avatar, or 0 if none.</summary>
    public static int GetWornItemColor(int itemHash, SaveData? save = null) => GetAvatar(save)?.ColorOf(itemHash) ?? 0;

    /// <summary>Number of owned clothing item hashes.</summary>
    public static int GetOwnedClothesCount(SaveData? save = null) => Save(save)?.ownedClothes?.Count ?? 0;

    /// <summary>True if the clothing hash is owned.</summary>
    public static bool OwnsClothes(int hash, SaveData? save = null) => Save(save)?.ownedClothes?.Contains(hash) == true;

    /// <summary>Adds a clothing hash to the owned list if not present. Returns true if added.</summary>
    public static bool AddOwnedClothes(int hash, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.ownedClothes ??= new List<int>();
        if (s.ownedClothes.Contains(hash)) return false;
        s.ownedClothes.Add(hash);
        return true;
    }

    /// <summary>Removes a clothing hash from the owned list. Returns true if removed.</summary>
    public static bool RemoveOwnedClothes(int hash, SaveData? save = null) => Save(save)?.ownedClothes?.Remove(hash) == true;

    /// <summary>MyTDULife data (mood, profile, boxes, photo), or null if no save.</summary>
    public static MyLifeData? GetMyLife(SaveData? save = null) => Save(save)?.myLife;

    /// <summary>Ensures and returns MyLife data for the save, or null if no save.</summary>
    public static MyLifeData? GetOrCreateMyLife(SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return null;
        return s.myLife ??= new MyLifeData();
    }

    /// <summary>Current mood label (e.g. "neutral").</summary>
    public static string GetMood(SaveData? save = null) => Save(save)?.myLife?.mood ?? "neutral";

    /// <summary>Sets the mood label and optional mood text. Returns false if there is no save.</summary>
    public static bool SetMood(string mood, string? moodText = null, SaveData? save = null)
    {
        var life = GetOrCreateMyLife(save);
        if (life == null) return false;
        life.mood = string.IsNullOrEmpty(mood) ? "neutral" : mood;
        if (moodText != null) life.moodText = moodText;
        return true;
    }

    /// <summary>Story progress step (-1 = never started).</summary>
    public static int GetStoryStep(SaveData? save = null) => Save(save)?.story ?? -1;

    /// <summary>Sets the story progress step. Returns false if there is no save.</summary>
    public static bool SetStoryStep(int step, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.story = step;
        return true;
    }

    /// <summary>True if the starter has been chosen.</summary>
    public static bool HasChosenStarter(SaveData? save = null) => Save(save)?.starterChosen == true;

    /// <summary>Sets whether the starter has been chosen. Returns false if there is no save.</summary>
    public static bool SetStarterChosen(bool chosen = true, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.starterChosen = chosen;
        return true;
    }

    /// <summary>UTC timestamp string of the last save, or empty string.</summary>
    public static string GetSavedAt(SaveData? save = null) => Save(save)?.savedAt ?? "";

    /// <summary>Saved radio station hash (0 = off).</summary>
    public static int GetRadioStation(SaveData? save = null) => Save(save)?.radioStation ?? 0;

    /// <summary>Saved radio station name (e.g. "off", "hariba").</summary>
    public static string GetRadioStationName(SaveData? save = null) => Save(save)?.radioStationName ?? "";

    /// <summary>Saved radio volume (-1 = never set).</summary>
    public static int GetRadioVolume(SaveData? save = null) => Save(save)?.radioVolume ?? -1;

    /// <summary>Sets saved radio state. Returns false if there is no save.</summary>
    public static bool SetRadio(int stationHash, string? stationName, int volume, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.radioStation = stationHash;
        s.radioStationName = stationName ?? "";
        s.radioVolume = volume;
        return true;
    }

    /// <summary>Serialized options string from the save, or empty string.</summary>
    public static string GetOptions(SaveData? save = null) => Save(save)?.options ?? "";

    /// <summary>Sets the serialized options string. Returns false if there is no save.</summary>
    public static bool SetOptions(string? options, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.options = options ?? "";
        return true;
    }

    /// <summary>Serialized key bindings string from the save, or empty string.</summary>
    public static string GetKeys(SaveData? save = null) => Save(save)?.keys ?? "";

    /// <summary>Sets the serialized key bindings string. Returns false if there is no save.</summary>
    public static bool SetKeys(string? keys, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.keys = keys ?? "";
        return true;
    }

    /// <summary>Recently seen players list.</summary>
    public static IReadOnlyList<RecentEntry> GetRecentPlayers(SaveData? save = null)
        => (IReadOnlyList<RecentEntry>?)Save(save)?.recentPlayers ?? Array.Empty<RecentEntry>();

    /// <summary>Number of recently seen players.</summary>
    public static int GetRecentPlayersCount(SaveData? save = null) => Save(save)?.recentPlayers?.Count ?? 0;

    /// <summary>Friend news entries list.</summary>
    public static IReadOnlyList<FriendNewsEntry> GetFriendNews(SaveData? save = null)
        => (IReadOnlyList<FriendNewsEntry>?)Save(save)?.friendNews ?? Array.Empty<FriendNewsEntry>();

    /// <summary>Number of friend news entries.</summary>
    public static int GetFriendNewsCount(SaveData? save = null) => Save(save)?.friendNews?.Count ?? 0;

    /// <summary>Number of unread friend news entries.</summary>
    public static int GetUnreadFriendNewsCount(SaveData? save = null)
    {
        var list = Save(save)?.friendNews;
        if (list == null) return 0;
        int n = 0;
        foreach (var e in list)
            if (e != null && !e.read) n++;
        return n;
    }

    /// <summary>Highest seen news id in TDU Media.</summary>
    public static int GetNewsSeen(SaveData? save = null) => Save(save)?.newsSeen ?? 0;

    /// <summary>Highest seen updates id in TDU Media.</summary>
    public static int GetUpdatesSeen(SaveData? save = null) => Save(save)?.updatesSeen ?? 0;

    /// <summary>Sets the highest seen news/updates ids (keeps the maximum). Returns false if there is no save.</summary>
    public static bool SetMediaSeen(int newsSeen, int updatesSeen, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.newsSeen = Math.Max(s.newsSeen, newsSeen);
        s.updatesSeen = Math.Max(s.updatesSeen, updatesSeen);
        return true;
    }

    /// <summary>Owned sticker board hashes.</summary>
    public static IReadOnlyList<int> GetStickerBoards(SaveData? save = null)
        => (IReadOnlyList<int>?)Save(save)?.stickerBoards ?? Array.Empty<int>();

    /// <summary>True if the sticker board hash is owned.</summary>
    public static bool OwnsStickerBoard(int hash, SaveData? save = null) => Save(save)?.stickerBoards?.Contains(hash) == true;

    /// <summary>Adds a sticker board hash if not present. Returns true if added.</summary>
    public static bool AddStickerBoard(int hash, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return false;
        s.stickerBoards ??= new List<int>();
        if (s.stickerBoards.Contains(hash)) return false;
        s.stickerBoards.Add(hash);
        return true;
    }

    /// <summary>Favorite sticker colors (up to 6 slots, 0xRRGGBB, -1 = empty).</summary>
    public static IReadOnlyList<int> GetFavoriteColors(SaveData? save = null)
        => (IReadOnlyList<int>?)Save(save)?.favoriteColors ?? Array.Empty<int>();

    /// <summary>Sets a favorite sticker color slot (0-based). Ensures the list has enough slots. Returns false if no save or invalid slot.</summary>
    public static bool SetFavoriteColor(int slot, int color, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null || slot < 0) return false;
        s.favoriteColors ??= new List<int>();
        while (s.favoriteColors.Count <= slot) s.favoriteColors.Add(-1);
        s.favoriteColors[slot] = color;
        return true;
    }
}