using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Resources.Sheets;

/// <summary>
/// Reads the game's "HairMakeType" Excel sheet, which lists the hairstyles (and
/// facepaints) that are valid for each Race / Tribe / Gender combination. The
/// stock generated sheet does not expose these arrays in a usable form, so this
/// mirrors Brio's approach of reading the row layout via raw offsets.
///
/// Each entry in <see cref="HairStyles"/> points at a <see cref="CharaMakeCustomize"/>
/// row whose <c>FeatureID</c> is the value written to customize byte 0x06.
/// </summary>
[Sheet("HairMakeType")]
public struct HairMakeTypeSheet(ExcelPage page, uint offset, uint row) : IExcelRow<HairMakeTypeSheet>
{
    public const int EntryCount = 100;

    public readonly ExcelPage ExcelPage => page;
    public readonly uint RowOffset => offset;
    public readonly uint RowId => row;

    public RowRef<Race> Race { get; private set; }
    public RowRef<Tribe> Tribe { get; private set; }
    public byte Gender { get; private set; }

    public RowRef<CharaMakeCustomize>[] HairStyles = new RowRef<CharaMakeCustomize>[EntryCount];

    public static HairMakeTypeSheet Create(ExcelPage page, uint offset, uint row)
    {
        var sheet = new HairMakeTypeSheet(page, offset, row)
        {
            Race = new RowRef<Race>(page.Module, (uint)page.ReadInt32(offset + 4292), page.Language),
            Tribe = new RowRef<Tribe>(page.Module, (uint)page.ReadInt32(offset + 4296), page.Language),
            Gender = (byte)page.ReadInt8(offset + 4300),
        };

        for (int i = 0; i < EntryCount; i++)
            sheet.HairStyles[i] = new RowRef<CharaMakeCustomize>(
                page.Module,
                page.ReadUInt32((nuint)(offset + 0xC + (i * 4))),
                page.Language);

        return sheet;
    }
}
