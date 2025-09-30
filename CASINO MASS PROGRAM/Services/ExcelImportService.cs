using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CASINO_MASS_PROGRAM.Data;
using CASINO_MASS_PROGRAM.DTOs;
using CASINO_MASS_PROGRAM.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CASINO_MASS_PROGRAM.Services;

public class ExcelImportService
{
    private readonly AppDbContext _db;

    public ExcelImportService(AppDbContext db) => _db = db;

    // Expected headers (ALL required)
    private static readonly string[] RequiredHeaders = new[]
    {
        "SEGMENT",
        "Team Representative",
        "ID",
        "Month",
        "Settlement Doc",
        "No",
        "Member ID",
        "Member name",
        "Joined date",
        "Last gaming date",
        "Eligible (Y/N)",
        "Casino win/(loss)",
        "Award settlement"
    };

    public async Task<ImportSummaryDto> ImportAndValidateAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();
        var firstRow = ws.FirstRowUsed();
        var lastRow = ws.LastRowUsed();
        var headerRow = firstRow.RowNumber();
        var startRow = headerRow + 1;

        var headers = ws.Row(headerRow)
            .CellsUsed()
            .ToDictionary<IXLCell, int, string>(
                c => c.Address.ColumnNumber,
                c => c.GetString().Trim()
            );

        // Ensure all headers exist
        var headerNames = headers.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingHeaders = RequiredHeaders.Where(h => !headerNames.Contains(h)).ToList();
        if (missingHeaders.Count > 0)
            throw new InvalidOperationException("Missing required columns: " + string.Join(", ", missingHeaders));

        var batch = new ImportBatch
        {
            FileName = file.FileName,
            UploadedAt = DateTime.UtcNow,
            Status = "Validated",
            FileContent = ms.ToArray()
        };

        int total = 0, valid = 0, invalid = 0;

        for (int r = startRow; r <= lastRow.RowNumber(); r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            total++;

            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
            {
                var value = ws.Cell(r, h.Key).GetFormattedString().Trim();
                data[h.Value] = value;
            }

            var errors = ValidateRow(data);
            var isValid = errors.Count == 0;

            if (isValid) valid++; else invalid++;

            var importRow = new ImportRow
            {
                RowNumber = r,
                IsValid = isValid,
                RawJson = JsonSerializer.Serialize(data)
            };

            foreach (var e in errors)
                importRow.Errors.Add(new ImportCellError { Column = e.Column, Message = e.Message });

            batch.Rows.Add(importRow);
        }

        batch.TotalRows = total;
        batch.ValidRows = valid;
        batch.InvalidRows = invalid;

        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync();

        return new ImportSummaryDto
        {
            BatchId = batch.Id,
            FileName = batch.FileName,
            UploadedAt = batch.UploadedAt,
            Status = batch.Status,
            TotalRows = total,
            ValidRows = valid,
            InvalidRows = invalid,
            SampleErrors = batch.Rows.Where(r => !r.IsValid).Take(10)
                .Select(r => new RowErrorDto
                {
                    RowNumber = r.RowNumber,
                    Errors = r.Errors.Select(e => new CellErrorDto { Column = e.Column, Message = e.Message }).ToList()
                }).ToList()
        };
    }

    private static List<(string Column, string Message)> ValidateRow(Dictionary<string, string> data)
    {
        var errors = new List<(string, string)>();

        // All required
        foreach (var col in RequiredHeaders)
        {
            if (!data.TryGetValue(col, out var v) || string.IsNullOrWhiteSpace(v))
                errors.Add((col, "Required"));
        }

        // Stop early if missing requireds
        if (errors.Count > 0) return errors;

        // Month
        if (!TryParseMonth(data["Month"], out _))
            errors.Add(("Month", "Invalid month"));

        // No
        if (!int.TryParse(data["No"], out _))
            errors.Add(("No", "Invalid integer"));

        // Dates
        if (!TryParseDateOnly(data["Joined date"], out _))
            errors.Add(("Joined date", "Invalid date"));
        if (!TryParseDateOnly(data["Last gaming date"], out _))
            errors.Add(("Last gaming date", "Invalid date"));

        // Eligible
        if (!TryParseYesNo(data["Eligible (Y/N)"], out _))
            errors.Add(("Eligible (Y/N)", "Must be Y or N"));

        // Money
        if (!TryParseMoney(data["Casino win/(loss)"], out _))
            errors.Add(("Casino win/(loss)", "Invalid number"));
        if (!TryParseMoney(data["Award settlement"], out _))
            errors.Add(("Award settlement", "Invalid number"));

        // Note: ID duplicates are allowed (grouped), no duplicate check here.
        return errors;
    }

    private static bool TryParseDateOnly(string value, out DateOnly date)
    {
        // Empty => default today
        if (string.IsNullOrWhiteSpace(value))
        {
            date = DateOnly.FromDateTime(DateTime.Today);
            return true;
        }

        var v = value.Trim();

        // Explicit formats: dd/MM/yyyy and MM/dd/yyyy (single-digit variants allowed)
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy" };
        if (DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            date = DateOnly.FromDateTime(exact);
            return true;
        }

        // Excel OADate (serial number)
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa))
        {
            try
            {
                date = DateOnly.FromDateTime(DateTime.FromOADate(oa));
                return true;
            }
            catch
            {
                // ignore
            }
        }

        // Fallback broad parse
        if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }

        date = default;
        return false;
    }

    private static bool TryParseMonth(string value, out DateOnly monthStart)
    {
        monthStart = default;
        var v = value.Trim();

        if (DateTime.TryParse(v, out var dt))
        {
            monthStart = new DateOnly(dt.Year, dt.Month, 1);
            return true;
        }

        var m = Regex.Match(v, @"^(?<y>\d{4})-(?<m>\d{1,2})$");
        if (m.Success &&
            int.TryParse(m.Groups["y"].Value, out var y) &&
            int.TryParse(m.Groups["m"].Value, out var mo) &&
            mo is >= 1 and <= 12)
        {
            monthStart = new DateOnly(y, mo, 1);
            return true;
        }

        m = Regex.Match(v, @"^(?<m>\d{1,2})/(?<y>\d{4})$");
        if (m.Success &&
            int.TryParse(m.Groups["y"].Value, out y) &&
            int.TryParse(m.Groups["m"].Value, out mo) &&
            mo is >= 1 and <= 12)
        {
            monthStart = new DateOnly(y, mo, 1);
            return true;
        }

        return false;
    }

    private static bool TryParseYesNo(string value, out bool yes)
    {
        // Empty => default N
        if (string.IsNullOrWhiteSpace(value))
        {
            yes = false;
            return true;
        }

        var v = value.Trim().ToUpperInvariant();
        if (v == "Y") { yes = true; return true; }
        if (v == "N") { yes = false; return true; }

        yes = false;
        return false;
    }

    private static bool TryParseMoney(string value, out decimal number)
    {
        number = 0m;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var v = value.Trim();
        var negative = v.StartsWith("(") && v.EndsWith(")");
        v = v.Trim('(', ')');

        v = Regex.Replace(v, @"[^\d\.,\-]", "");
        v = v.Replace(",", "");

        if (v == "-")
        {
            v = "0";
            return true;
        }

        if (decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
        {
            number = negative ? -d : d;
            return true;
        }
        return false;
    }
}