using Implement.ApplicationDbContext;
using Implement.EntityModels;
using Implement.Services;
using Implement.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace CASINO_MASS_PROGRAM.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ImportExcelController : ControllerBase
{
    private readonly CasinoMassProgramDbContext _db;
    private readonly ExcelImportService _excel;

    public ImportExcelController(CasinoMassProgramDbContext db, ExcelImportService excel)
    {
        _db = db;
        _excel = excel;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ImportSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest("File is required.");
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only .xlsx files are supported.");

        var result = await _excel.ImportAndValidateAsync(file);
        return Ok(result);
    }

    [HttpGet("{batchId:guid}")]
    [ProducesResponseType(typeof(ImportSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary([FromRoute] Guid batchId)
    {
        var batch = await _db.ImportBatches
            .AsNoTracking()
            .Where(b => b.Id == batchId)
            .Select(b => new ImportSummaryDto
            {
                BatchId = b.Id,
                FileName = b.FileName,
                UploadedAt = b.UploadedAt,
                Status = b.Status,
                TotalRows = b.TotalRows,
                ValidRows = b.ValidRows,
                InvalidRows = b.InvalidRows,
                SampleErrors = b.Rows.Where(r => !r.IsValid).Take(10).Select(r => new RowErrorDto
                {
                    RowNumber = r.RowNumber,
                    Errors = r.Errors.Select(e => new CellErrorDto { Column = e.Column, Message = e.Message }).ToList()
                }).ToList()
            })
            .FirstOrDefaultAsync();

        return batch is null ? NotFound() : Ok(batch);
    }

    [HttpGet("{batchId:guid}/annotated")]
    public async Task<IActionResult> DownloadAnnotated([FromRoute] Guid batchId)
    {
        var batch = await _db.ImportBatches
            .Include(b => b.Rows).ThenInclude(r => r.Errors)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch is null) return NotFound();
        if (batch.FileContent is null || batch.FileContent.Length == 0)
            return BadRequest("Original file content is not available.");

        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(batch.FileContent));
        var ws = wb.Worksheets.First();
        var headerRow = ws.FirstRowUsed();

        var headers = headerRow.Cells().ToDictionary(
            c => c.Address?.ColumnNumber ?? 0,
            c => (c.GetString() ?? string.Empty).Trim()
        );

        var errorMap = batch.Rows
            .Where(r => !r.IsValid)
            .ToDictionary(
                r => r.RowNumber,
                r => r.Errors.Select(e => (e.Column, e.Message)).ToList());

        var lastCol = ws.LastColumnUsed().ColumnNumber();
        var errorsColIdx = lastCol + 1;
        ws.Cell(1, errorsColIdx).Value = "__Errors";

        foreach (var kvp in errorMap)
        {
            var rowIdx = kvp.Key;
            var xlRow = ws.Row(rowIdx);
            if (xlRow.IsEmpty()) continue;

            foreach (var (columnName, _) in kvp.Value)
            {
                var colIdx = headers
                    .Where(h => string.Equals(h.Value, columnName, StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Key)
                    .FirstOrDefault();

                if (colIdx > 0)
                {
                    var cell = ws.Cell(rowIdx, colIdx);
                    cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightPink;
                    cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.DarkRed;
                }
            }

            ws.Cell(rowIdx, errorsColIdx).Value = string.Join(" | ", kvp.Value.Select(e => $"{e.Column}: {e.Message}"));
            ws.Cell(rowIdx, errorsColIdx).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.Yellow;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var fileName = System.IO.Path.GetFileNameWithoutExtension(batch.FileName) + "_annotated.xlsx";
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // Commit with grouping by Team Representative "ID"
    [HttpPost("{batchId:guid}/commit")]
    public async Task<IActionResult> Commit([FromRoute] Guid batchId)
    {
        var batch = await _db.ImportBatches
            .Include(b => b.Rows)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch is null) return NotFound();
        if (!string.Equals(batch.Status, "Validated", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Batch must be in 'Validated' status.");

        var toInsertSettlements = new List<AwardSettlement>();
        var upsertedReps = new Dictionary<string, TeamRepresentative>(StringComparer.OrdinalIgnoreCase);
        var upsertedMembers = new Dictionary<string, Member>(StringComparer.OrdinalIgnoreCase);
        var trmPairs = new HashSet<(Guid RepId, Guid MemberId)>();

        // Preload existing reps and members to reduce DB round-trips
        var existingReps = await _db.TeamRepresentatives.ToListAsync();
        foreach (var r in existingReps) upsertedReps[r.ExternalId] = r;

        var existingMembers = await _db.Members.ToListAsync();
        foreach (var m in existingMembers) upsertedMembers[m.MemberCode] = m;

        foreach (var row in batch.Rows.Where(r => r.IsValid))
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawJson) ?? new();
            string Get(string key) => data.TryGetValue(key, out var v) ? v?.Trim() ?? "" : "";

            // Parse required typed values
            if (!ExcelImportService_TryParseMonth(Get("Month"), out var monthStart)) continue;
            if (!ExcelImportService_TryParseDateOnly(Get("Joined date"), out var joinedDate)) continue;
            if (!ExcelImportService_TryParseDateOnly(Get("Last gaming date"), out var lastGamingDate)) continue;
            if (!ExcelImportService_TryParseYesNo(Get("Eligible (Y/N)"), out var eligible)) continue;
            if (!ExcelImportService_TryParseMoney(Get("Casino win/(loss)"), out var casinoWinLoss)) continue;
            if (!ExcelImportService_TryParseMoney(Get("Award settlement"), out var awardSettlement)) continue;
            if (!int.TryParse(Get("No"), out var noValue)) continue;

            // Upsert TeamRepresentative by ExternalId = "ID"
            var repExternalId = Get("ID");
            if (!upsertedReps.TryGetValue(repExternalId, out var rep))
            {
                rep = new TeamRepresentative
                {
                    ExternalId = repExternalId,
                    Name = Get("Team Representative"),
                    Segment = Get("SEGMENT")
                };
                _db.TeamRepresentatives.Add(rep);
                upsertedReps[repExternalId] = rep;
            }
            else
            {
                // Keep existing values; if empty, fill from current row
                if (string.IsNullOrWhiteSpace(rep.Name)) rep.Name = Get("Team Representative");
                if (string.IsNullOrWhiteSpace(rep.Segment)) rep.Segment = Get("SEGMENT");
            }

            // Upsert Member by MemberCode = "Member ID"
            var memberCode = Get("Member ID");
            if (!upsertedMembers.TryGetValue(memberCode, out var member))
            {
                member = new Member
                {
                    MemberCode = memberCode,
                    FullName = Get("Member name")
                };
                _db.Members.Add(member);
                upsertedMembers[memberCode] = member;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(member.FullName))
                    member.FullName = Get("Member name");
            }

            // Link TR <-> Member in join table (dedupe in-memory)
            trmPairs.Add((rep.Id, member.Id));

            // Per-row settlement
            var settlement = new AwardSettlement
            {
                TeamRepresentative = rep,
                Member = member,
                MonthStart = monthStart,
                SettlementDoc = Get("Settlement Doc"),
                No = noValue,
                JoinedDate = joinedDate,
                LastGamingDate = lastGamingDate,
                Eligible = eligible,
                CasinoWinLoss = casinoWinLoss,
                AwardSettlementAmount = awardSettlement
            };
            toInsertSettlements.Add(settlement);
        }

        // Persist all changes in a single SaveChanges
        // Insert join rows (avoid duplicates)
        foreach (var pair in trmPairs)
        {
            var exists = await _db.TeamRepresentativeMembers
                .AnyAsync(x => x.TeamRepresentativeId == pair.RepId && x.MemberId == pair.MemberId);
            if (!exists)
            {
                _db.TeamRepresentativeMembers.Add(new TeamRepresentativeMember
                {
                    TeamRepresentativeId = pair.RepId,
                    MemberId = pair.MemberId
                });
            }
        }

        _db.AwardSettlements.AddRange(toInsertSettlements);
        batch.Status = "Committed";
        await _db.SaveChangesAsync();

        return Ok(new
        {
            representatives = upsertedReps.Count,
            members = upsertedMembers.Count,
            links = trmPairs.Count,
            settlementsInserted = toInsertSettlements.Count
        });
    }

    // Fix for CS0120 and CS0177: Replace the problematic ExcelImportService_TryParseDateOnly method implementation

    private static bool ExcelImportService_TryParseDateOnly(string value, out DateOnly d)
        => TryParseDateOnly(value, out d);

    private static bool TryParseDateOnly(string value, out DateOnly date)
    {
        date = default;
        if (DateOnly.TryParse(value, out date)) return true;
        if (DateTime.TryParse(value, out var dt)) { date = DateOnly.FromDateTime(dt); return true; }
        return false;
    }

    private static bool ExcelImportService_TryParseMonth(string value, out DateOnly monthStart) => TryParseMonth(value, out monthStart);
    private static bool ExcelImportService_TryParseYesNo(string value, out bool yes) => TryParseYesNo(value, out yes);
    private static bool ExcelImportService_TryParseMoney(string value, out decimal number) => TryParseMoney(value, out number);

    private static bool TryParseMonth(string value, out DateOnly monthStart)
    {
        monthStart = default;
        var v = value.Trim();
        if (DateTime.TryParse(v, out var dt)) { monthStart = new DateOnly(dt.Year, dt.Month, 1); return true; }
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^(?<y>\d{4})-(?<m>\d{1,2})$");
        if (m.Success && int.TryParse(m.Groups["y"].Value, out var y) && int.TryParse(m.Groups["m"].Value, out var mo) && mo is >= 1 and <= 12)
        { monthStart = new DateOnly(y, mo, 1); return true; }
        m = System.Text.RegularExpressions.Regex.Match(v, @"^(?<m>\d{1,2})/(?<y>\d{4})$");
        if (m.Success && int.TryParse(m.Groups["y"].Value, out y) && int.TryParse(m.Groups["m"].Value, out mo) && mo is >= 1 and <= 12)
        { monthStart = new DateOnly(y, mo, 1); return true; }
        return false;
    }

    private static bool TryParseYesNo(string value, out bool yes)
    {
        yes = false;
        var v = value.Trim().ToUpperInvariant();
        if (v == "Y") { yes = true; return true; }
        if (v == "N") { yes = false; return true; }
        return false;
    }

    private static bool TryParseMoney(string value, out decimal number)
    {
        number = 0m;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        var negative = v.StartsWith("(") && v.EndsWith(")");
        v = v.Trim('(', ')');
        v = System.Text.RegularExpressions.Regex.Replace(v, @"[^\d\.,\-]", "");
        v = v.Replace(",", "");
        if (decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
        { number = negative ? -d : d; return true; }
        return false;
    }
}