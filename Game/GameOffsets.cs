namespace R2PrismRuntime.Game;

/// <summary>
/// Every offset and signature used to reach the prisms, ported from the Cheat Engine table
/// (Remnant 2_v3.1_Released.CT, "Manage Prisms..." script). The game is no longer updated,
/// so these target the final build.
/// </summary>
public static class GameOffsets
{
    // ── Pointer chain ────────────────────────────────────────────
    // CT: [[[[[[pGEngine]]+0xFC0]+0x38]]+0x30]+0x2D0]+Inventory]
    // [pGEngine] holds the address of the GEngine global, so one dereference of the
    // global yields the UGameEngine object.
    //   GEngine
    //     +0xFC0  UGameInstance*
    //     +0x38   TArray<ULocalPlayer*> LocalPlayers (Data; Num at +0x40)
    //     [0]     ULocalPlayer*
    //     +0x30   APlayerController*
    //     +0x2D0  APawn* (player character)
    //     +Inv    inventory component
    public const ulong GEngine_GameInstance      = 0xFC0;
    public const ulong GameInstance_LocalPlayers = 0x038;
    public const ulong LocalPlayer_PC            = 0x030;
    public const ulong PC_Pawn                   = 0x2D0;

    /// Pawn → inventory component. The CT uses 0xB48 on the latest build (v444) and 0xB40 on
    /// older ones; both are tried and the one whose class name contains "Inventory" wins.
    public static readonly ulong[] InventoryOffsets = { 0xB48, 0xB40 };

    // ── Inventory ────────────────────────────────────────────────
    public const ulong Inv_DataPtr       = 0x1D8;  // items TArray.Data
    public const ulong Inv_Count         = 0x1E0;  // items TArray.Num

    public const ulong Item_BlueprintPtr = 0x08;   // item definition object
    public const ulong Item_DataPtr      = 0x18;   // item instance data
    public const ulong Item_Stride       = 0x28;

    public const ulong BP_Class          = 0x110;  // [bp+0x110] = class, +0x18 = FName
    public const ulong Class_NameId      = 0x018;
    // CT: display name = wide string at [[[bp+0x110]+0x300]+0x30]
    public const ulong Class_DisplayText = 0x300;
    public const ulong Text_StringPtr    = 0x030;

    // ── Prism instance data ──────────────────────────────────────
    public const ulong Data_Level        = 0x28;   // internal level (int32)
    public const ulong Data_SegPtr       = 0x58;   // CurrentSegments TArray.Data
    public const ulong Data_SegCount     = 0x60;
    public const ulong Data_FeedPtr      = 0x70;   // CurrentFeedData ("Roll Chances" in the CT)
    public const ulong Data_FeedCount    = 0x78;
    public const ulong Data_Xp           = 0x84;   // PendingExperience (float)

    // ── Segment (CurrentSegments element) ────────────────────────
    public const ulong Seg_RowName       = 0x00;   // FName (ComparisonIndex, Number)
    public const ulong Seg_Level         = 0x08;   // int32
    public const ulong Seg_Object        = 0x20;   // cached UObject* for the row (may be null)
    public const ulong Seg_Stride        = 0x28;

    // ── Fed fragment (CurrentFeedData element) ───────────────────
    public const ulong Feed_RowName      = 0x00;   // FName
    public const ulong Feed_Level        = 0x08;   // FedLevel (int32)
    public const ulong Feed_Stride       = 0x0C;

    // ── UObjectBase (UE5, 64-bit) ─────────────────────────────────
    public const ulong UObject_VTable    = 0x00;
    public const ulong UObject_Class     = 0x10;
    public const ulong UObject_Name      = 0x18;
    public const ulong UObject_Outer     = 0x20;

    // ── Signatures ───────────────────────────────────────────────

    /// "48 03 F8 44 89 44 24 38": the 7 bytes before it are a RIP-relative instruction
    /// referencing the FNamePool.
    public static readonly byte[] AobFNamePool = { 0x48, 0x03, 0xF8, 0x44, 0x89, 0x44, 0x24, 0x38 };

    /// "49 8B D7 48 8B 01 FF 90 D8 02 00 00" = mov rdx,r15; mov rax,[rcx]; call [rax+2D8].
    /// The 7 bytes before it are "mov rcx,[rip+disp32]" loading GEngine.
    public static readonly byte[] AobGEngine = { 0x49, 0x8B, 0xD7, 0x48, 0x8B, 0x01, 0xFF, 0x90, 0xD8, 0x02, 0x00, 0x00 };
}
