using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Catalog;

/// <summary>Bulk import and export of products, variants and opening stock as CSV or Excel (FR-B09), through the real API.</summary>
public class ProductImportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    private static string U() => Guid.NewGuid().ToString("N")[..8];

    private static string BranchCode() => "I" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private const string Header = "name_en,category,regular_price,sale_price,brand,sku,color_name,size,opening_stock,branch_code";

    private static string Csv(params string[] lines) => string.Join("\n", lines);

    private async Task<HttpResponseMessage> UploadAsync(string token, byte[] content, string fileName, string query = "", Guid? branch = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/catalog/products/import" + query) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (branch is { } b)
        {
            request.Headers.Add("X-Branch-Id", b.ToString());
        }

        return await _api.Client.SendAsync(request, Ct);
    }

    private async Task<JsonElement> ImportAsync(string token, string csv, string query = "", HttpStatusCode expected = HttpStatusCode.OK, Guid? branch = null) =>
        await TestApi.ExpectAsync(await UploadAsync(token, Encoding.UTF8.GetBytes(csv), "products.csv", query, branch), expected);

    private static IEnumerable<JsonElement> Issues(JsonElement report, string severity) =>
        report.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("severity").GetString() == severity);

    private async Task<JsonElement> ProductBySlugAsync(string token, string slug)
    {
        var list = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products?search={slug}", token), HttpStatusCode.OK);
        var item = list.GetProperty("items").EnumerateArray().FirstOrDefault(i => i.GetProperty("slug").GetString() == slug);
        if (item.ValueKind == JsonValueKind.Undefined)
        {
            return default;
        }

        return await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{item.GetProperty("id").GetGuid()}", token), HttpStatusCode.OK);
    }

    private async Task<(string Token, string Branch, Guid BranchId)> AdminAndBranchAsync()
    {
        var admin = await _api.LoginAdminAsync();
        var code = BranchCode();
        var id = await _api.CreateBranchAsync(admin.Access, code, "Import branch");
        return (admin.Access, code, id);
    }

    private static string Row(string name, string sku, string color = "Red", string size = "M", string price = "1000", string sale = "", string brand = "",
        string stock = "", string branch = "", string category = "Clothing > T-shirts") =>
        $"{name},{category},{price},{sale},{brand},{sku},{color},{size},{stock},{branch}";

    // ---- importing ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_creates_products_variants_a_brand_and_opening_stock_and_the_report_says_so()
    {
        var (token, branch, branchId) = await AdminAndBranchAsync();
        var name = "Import Tee " + U();
        var sku = "IMP-" + U().ToUpperInvariant();
        var brand = "Brand " + U();

        var report = await ImportAsync(token, Csv(Header,
            Row(name, sku + "-M", "Red", "M", "1000", "850", brand, "7", branch),
            Row(name, sku + "-L", "Red", "L", "1000", "850", brand, "3", branch),
            Row("Import Wallet " + U(), "IMPW-" + U().ToUpperInvariant(), "Brown", "Free size", "1500", category: "Purses > Wallets")));

        Assert.True(report.GetProperty("committed").GetBoolean(), report.ToString());
        Assert.Equal((2, 3, 0), (report.GetProperty("productsCreated").GetInt32(), report.GetProperty("variantsCreated").GetInt32(), report.GetProperty("errorCount").GetInt32()));
        Assert.Equal(2, report.GetProperty("stockLinesApplied").GetInt32());
        Assert.Contains(Issues(report, "warning"), i => i.GetProperty("message").GetString()!.Contains("was created", StringComparison.Ordinal));

        var product = await ProductBySlugAsync(token, SlugOf(name));
        Assert.Equal(850m, product.GetProperty("salePrice").GetDecimal());
        Assert.Equal("Draft", product.GetProperty("status").GetString());
        Assert.Equal(2, product.GetProperty("variants").GetArrayLength());
        Assert.NotNull(product.GetProperty("brandId").GetGuid().ToString());

        var stock = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={branchId}&search={sku}", token), HttpStatusCode.OK);
        Assert.Equal([3, 7], stock.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("onHand").GetInt32()).Order().ToList());
    }

    private static string SlugOf(string name) => string.Join('-', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public async Task A_dry_run_validates_everything_but_saves_nothing()
    {
        var (token, branch, branchId) = await AdminAndBranchAsync();
        var name = "Dry Tee " + U();
        var sku = "DRY-" + U().ToUpperInvariant();

        var report = await ImportAsync(token, Csv(Header, Row(name, sku, stock: "5", branch: branch)), "?dryRun=true");

        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.False(report.GetProperty("committed").GetBoolean());
        Assert.Equal((1, 1, 0), (report.GetProperty("productsCreated").GetInt32(), report.GetProperty("variantsCreated").GetInt32(), report.GetProperty("errorCount").GetInt32()));
        Assert.Equal(default, (await ProductBySlugAsync(token, SlugOf(name))).ValueKind);
        var stock = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={branchId}&search={sku}", token), HttpStatusCode.OK);
        Assert.Empty(stock.GetProperty("items").EnumerateArray());

        // The same file then imports for real.
        var real = await ImportAsync(token, Csv(Header, Row(name, sku, stock: "5", branch: branch)));
        Assert.True(real.GetProperty("committed").GetBoolean());
    }

    [Fact]
    public async Task One_bad_row_rejects_the_whole_file_and_the_report_names_the_row_and_column()
    {
        var (token, branch, _) = await AdminAndBranchAsync();
        var good = "Good Tee " + U();
        var bad = "Bad Tee " + U();

        var report = await ImportAsync(token, Csv(Header,
            Row(good, "G-" + U().ToUpperInvariant()),                                        // row 2: fine
            Row(bad, "B-" + U().ToUpperInvariant(), price: "500", sale: "900"),             // row 3: sale price above regular price
            Row("Cat Tee " + U(), "C-" + U().ToUpperInvariant(), category: "No Such Category"), // row 4
            Row("Num Tee " + U(), "N-" + U().ToUpperInvariant(), price: "12abc")));          // row 5

        Assert.False(report.GetProperty("committed").GetBoolean());
        var errors = Issues(report, "error").ToList();
        Assert.Contains(errors, e => e.GetProperty("row").GetInt32() == 3 && e.GetProperty("column").GetString() == "sale_price");
        Assert.Contains(errors, e => e.GetProperty("row").GetInt32() == 4 && e.GetProperty("column").GetString() == "category");
        Assert.Contains(errors, e => e.GetProperty("row").GetInt32() == 5 && e.GetProperty("column").GetString() == "regular_price");
        Assert.Equal(default, (await ProductBySlugAsync(token, SlugOf(good))).ValueKind); // the good row was rolled back too
    }

    [Fact]
    public async Task Row_problems_cover_duplicate_skus_bad_booleans_dates_and_unknown_branches()
    {
        var (token, _, _) = await AdminAndBranchAsync();
        var sku = "DUP-" + U().ToUpperInvariant();
        var header = "name_en,category,regular_price,sku,is_new,sale_start,sale_end,sale_price,opening_stock,branch_code,mystery";

        var report = await ImportAsync(token, Csv(header,
            $"Dup A {U()},Clothing > T-shirts,100,{sku},maybe,,,,,,x",
            $"Dup B {U()},Clothing > T-shirts,100,{sku},no,,,,,,",
            $"Dates {U()},Clothing > T-shirts,100,S-{U()},no,31-12-2026x,,,,,",
            $"Stock {U()},Clothing > T-shirts,100,T-{U()},no,,,,5,NOPE,"));

        var errors = Issues(report, "error").Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("column").GetString())).ToList();
        Assert.Contains((2, "is_new"), errors);
        Assert.Contains((3, "sku"), errors);
        Assert.Contains((4, "sale_start"), errors);
        Assert.Contains((5, "branch_code"), errors);
        Assert.Contains(Issues(report, "warning"), w => w.GetProperty("column").GetString() == "mystery");
        Assert.False(report.GetProperty("committed").GetBoolean());
    }

    [Fact]
    public async Task Create_mode_refuses_an_existing_product_and_upsert_updates_it_keeping_blank_cells()
    {
        var (token, _, _) = await AdminAndBranchAsync();
        var name = "Upsert Tee " + U();
        var sku = "UPS-" + U().ToUpperInvariant();
        await ImportAsync(token, Csv(Header, Row(name, sku + "-M", price: "1000", brand: "Keep Brand " + U())));

        var refused = await ImportAsync(token, Csv(Header, Row(name, sku + "-M")));
        Assert.False(refused.GetProperty("committed").GetBoolean());
        Assert.Contains(Issues(refused, "error"), e => e.GetProperty("message").GetString()!.Contains("already exists", StringComparison.Ordinal));

        // Upsert: new price and sale, a new variant; the blank brand keeps the old brand.
        var report = await ImportAsync(token, Csv(Header,
            Row(name, sku + "-M", price: "1200", sale: "999"),
            Row(name, sku + "-XL", size: "XL", price: "1200", sale: "999")), "?mode=upsert");

        Assert.True(report.GetProperty("committed").GetBoolean(), report.ToString());
        Assert.Equal((0, 1, 1), (report.GetProperty("productsCreated").GetInt32(), report.GetProperty("productsUpdated").GetInt32(), report.GetProperty("variantsCreated").GetInt32()));
        var product = await ProductBySlugAsync(token, SlugOf(name));
        Assert.Equal((1200m, 999m), (product.GetProperty("regularPrice").GetDecimal(), product.GetProperty("salePrice").GetDecimal()));
        Assert.Equal(2, product.GetProperty("variants").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, product.GetProperty("brandId").ValueKind);

        // Nothing changed this time: nothing is updated.
        var again = await ImportAsync(token, Csv(Header, Row(name, sku + "-M", price: "1200", sale: "999")), "?mode=upsert");
        Assert.Equal((0, 0, 0, 0), (again.GetProperty("productsCreated").GetInt32(), again.GetProperty("productsUpdated").GetInt32(),
            again.GetProperty("variantsCreated").GetInt32(), again.GetProperty("variantsUpdated").GetInt32()));
    }

    [Fact]
    public async Task Opening_stock_never_overwrites_existing_stock_history()
    {
        var (token, branch, branchId) = await AdminAndBranchAsync();
        var name = "History Tee " + U();
        var sku = "HIS-" + U().ToUpperInvariant();
        await ImportAsync(token, Csv(Header, Row(name, sku, stock: "4", branch: branch)));

        var report = await ImportAsync(token, Csv(Header, Row(name, sku, stock: "50", branch: branch)), "?mode=upsert");

        Assert.True(report.GetProperty("committed").GetBoolean());
        Assert.Equal(0, report.GetProperty("stockLinesApplied").GetInt32());
        Assert.Contains(Issues(report, "warning"), w => w.GetProperty("column").GetString() == "opening_stock");
        var stock = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={branchId}&search={sku}", token), HttpStatusCode.OK);
        Assert.Equal(4, Assert.Single(stock.GetProperty("items").EnumerateArray()).GetProperty("onHand").GetInt32());
    }

    [Fact]
    public async Task Opening_stock_without_a_branch_code_uses_the_active_branch_and_otherwise_asks_for_one()
    {
        var (token, _, branchId) = await AdminAndBranchAsync();
        var name = "Active Tee " + U();
        var sku = "ACT-" + U().ToUpperInvariant();

        var missing = await ImportAsync(token, Csv(Header, Row(name, sku, stock: "5")));
        Assert.Contains(Issues(missing, "error"), e => e.GetProperty("column").GetString() == "branch_code");

        var viaHeader = await ImportAsync(token, Csv(Header, Row(name, sku, stock: "5")), branch: branchId);
        Assert.True(viaHeader.GetProperty("committed").GetBoolean(), viaHeader.ToString());
    }

    // ---- files that are not usable ------------------------------------------------------------------------

    [Fact]
    public async Task Unusable_files_are_refused_with_a_clear_message()
    {
        var admin = (await _api.LoginAdminAsync()).Access;

        var noColumns = await UploadAsync(admin, Encoding.UTF8.GetBytes("name_en,sku\nTee,X1"), "a.csv");
        Assert.Equal(HttpStatusCode.BadRequest, noColumns.StatusCode);
        Assert.Contains("category", await noColumns.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, [], "empty.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, [0xFF, 0xFE, 0x00, 0x01, 0xC3, 0x28], "binary.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, Encoding.UTF8.GetBytes("name_en,category,regular_price\n\"Tee,Clothing,1"), "quote.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, "PK\u0003\u0004 not really a zip"u8.ToArray(), "fake.xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, new byte[(5 * 1024 * 1024) + 10], "big.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, Encoding.UTF8.GetBytes("name_en,category,regular_price,name_en\nA,B,1,C"), "dup.csv")).StatusCode);
    }

    // ---- permissions --------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_and_export_need_their_permissions_and_stock_needs_the_adjust_permission_in_that_branch()
    {
        var admin = (await _api.LoginAdminAsync()).Access;
        var branchCode = BranchCode();
        var branch = await _api.CreateBranchAsync(admin, branchCode, "Perm branch");
        var viewer = await _api.CreateRoleAsync(admin, "Viewer-" + U(), "catalog.product.view");
        var importer = await _api.CreateRoleAsync(admin, "Importer-" + U(), "catalog.product.view", "catalog.product.import", "catalog.product.export");
        var withStock = await _api.CreateRoleAsync(admin, "ImporterStock-" + U(), "catalog.product.view", "catalog.product.import", "inventory.stock.adjust", "inventory.stock.view");
        var v = await _api.CreateUserAsync(admin, $"viewer-{U()}@juvo.test", "Viewer", false, (branch, viewer));
        var i = await _api.CreateUserAsync(admin, $"importer-{U()}@juvo.test", "Importer", false, (branch, importer));
        var s = await _api.CreateUserAsync(admin, $"stocker-{U()}@juvo.test", "Stocker", false, (branch, withStock));
        var viewerToken = (await _api.LoginAsync(v.Email, v.Password)).Access;
        var importerToken = (await _api.LoginAsync(i.Email, i.Password)).Access;
        var stockerToken = (await _api.LoginAsync(s.Email, s.Password)).Access;

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(viewerToken, Encoding.UTF8.GetBytes(Csv(Header, Row("No " + U(), "NO-" + U()))), "a.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/catalog/products/export", viewerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/catalog/products/import-template", viewerToken)).StatusCode);

        // An importer without stock rights can import products, but opening stock is reported as an error.
        var sku = "PRM-" + U().ToUpperInvariant();
        var refused = await ImportAsync(importerToken, Csv(Header, Row("Perm Tee " + U(), sku, stock: "5", branch: branchCode)));
        Assert.False(refused.GetProperty("committed").GetBoolean());
        Assert.Contains(Issues(refused, "error"), e => e.GetProperty("column").GetString() == "opening_stock");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(importerToken, Encoding.UTF8.GetBytes(Csv(Header, Row("Perm Plain " + U(), "PRP-" + U()))), "a.csv")).StatusCode);

        // Stock into a branch the user does not work in is refused.
        var other = BranchCode();
        await _api.CreateBranchAsync(admin, other, "Other branch");
        var elsewhere = await ImportAsync(stockerToken, Csv(Header, Row("Elsewhere " + U(), "ELS-" + U(), stock: "5", branch: other)));
        Assert.False(elsewhere.GetProperty("committed").GetBoolean());

        var ok = await ImportAsync(stockerToken, Csv(Header, Row("Allowed " + U(), "ALW-" + U(), stock: "5", branch: branchCode)));
        Assert.True(ok.GetProperty("committed").GetBoolean(), ok.ToString());
    }

    // ---- exporting ----------------------------------------------------------------------------------------

    private async Task<(string Text, HttpResponseMessage Response)> ExportAsync(string token, string query)
    {
        var response = await _api.GetAsync("/api/v1/catalog/products/export" + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct)), response);
    }

    [Fact]
    public async Task Export_lists_variants_with_stock_of_one_branch_and_can_be_imported_again_without_changes()
    {
        var (token, branch, branchId) = await AdminAndBranchAsync();
        var name = "Round Trip Tee " + U();
        var sku = "RTR-" + U().ToUpperInvariant();
        await ImportAsync(token, Csv(Header, Row(name, sku + "-M", price: "1000", sale: "800", brand: "RT Brand " + U(), stock: "6", branch: branch), Row(name, sku + "-L", size: "L", price: "1000", sale: "800")));

        var (text, response) = await ExportAsync(token, $"?branchId={branchId}");

        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("﻿slug,", text, StringComparison.Ordinal); // BOM so Excel reads UTF-8
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
        var header = lines[0].TrimStart('﻿').Split(',').ToList();
        var mine = lines.Where(l => l.Contains(sku, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mine.Count);
        var cells = mine.First(l => l.Contains(sku + "-M", StringComparison.Ordinal)).Split(',');
        Assert.Equal("6", cells[header.IndexOf("on_hand")]);
        Assert.Equal(branch, cells[header.IndexOf("branch_code")]);
        Assert.Equal("Clothing > T-shirts", string.Join(',', cells[header.IndexOf("category")..(header.IndexOf("category") + 1)]).Trim('"'));

        // The exported file goes back in as an update with nothing to change.
        var report = await ImportAsync(token, text, "?mode=upsert");
        Assert.True(report.GetProperty("committed").GetBoolean(), report.ToString());
        Assert.Equal(0, report.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, report.GetProperty("productsUpdated").GetInt32());
    }

    [Fact]
    public async Task Cost_price_is_exported_only_to_people_who_may_see_it_and_other_branches_stock_is_not_exportable()
    {
        var admin = (await _api.LoginAdminAsync()).Access;
        var a = await _api.CreateBranchAsync(admin, BranchCode(), "Export A");
        var b = await _api.CreateBranchAsync(admin, BranchCode(), "Export B");
        var noCost = await _api.CreateRoleAsync(admin, "Export-" + U(), "catalog.product.view", "catalog.product.export", "inventory.stock.view");
        var user = await _api.CreateUserAsync(admin, $"exporter-{U()}@juvo.test", "Exporter", false, (a, noCost));
        var token = (await _api.LoginAsync(user.Email, user.Password)).Access;

        var (adminText, _) = await ExportAsync(admin, string.Empty);
        var (userText, _) = await ExportAsync(token, string.Empty);
        Assert.Contains("cost_price", adminText.Split("\r\n")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("cost_price", userText.Split("\r\n")[0], StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync($"/api/v1/catalog/products/export?branchId={a}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/catalog/products/export?branchId={b}", token)).StatusCode);
    }

    [Fact]
    public async Task The_template_is_a_valid_file_in_both_formats_and_imports_cleanly()
    {
        var admin = (await _api.LoginAdminAsync()).Access;
        var csv = await _api.GetAsync("/api/v1/catalog/products/import-template", admin);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var csvBytes = await csv.Content.ReadAsByteArrayAsync(Ct);
        Assert.Contains("juvo-product-import-template.csv", csv.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);

        var xlsx = await _api.GetAsync("/api/v1/catalog/products/import-template?format=xlsx", admin);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        AssertValidWorkbook(await xlsx.Content.ReadAsByteArrayAsync(Ct));

        // The template (with its example rows) imports as it is; the branch it names must exist, so the report may only complain about that.
        var report = await TestApi.ExpectAsync(await UploadAsync(admin, csvBytes, "template.csv", "?dryRun=true"), HttpStatusCode.OK);
        Assert.Equal(0, report.GetProperty("errorCount").GetInt32());
        Assert.Equal((2, 3), (report.GetProperty("productsCreated").GetInt32(), report.GetProperty("variantsCreated").GetInt32()));
    }

    // ---- Excel --------------------------------------------------------------------------------------------

    private static void AssertValidWorkbook(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Description)));
    }

    [Fact]
    public async Task Excel_export_is_a_valid_workbook_and_imports_back_with_numbers_and_bengali_text_intact()
    {
        var (token, branch, _) = await AdminAndBranchAsync();
        var name = "Excel Tee " + U();
        var sku = "XLS-" + U().ToUpperInvariant();
        await ImportAsync(token, Csv(Header, Row(name, sku, price: "1250.5", stock: "9", branch: branch)));
        var nameBn = "এক্সেল টি-শার্ট " + U();
        var product = await ProductBySlugAsync(token, SlugOf(name));
        await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/catalog/products/{product.GetProperty("id").GetGuid()}", new
        {
            nameEn = name, nameBn, categoryId = product.GetProperty("categoryId").GetGuid(), regularPrice = 1250.5, targetGroup = "Unisex",
            isNew = false, isFeatured = false, isBestSeller = false,
        }, token), HttpStatusCode.OK);

        var response = await _api.GetAsync("/api/v1/catalog/products/export?format=xlsx", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        AssertValidWorkbook(bytes);

        var table = factory.Services.GetRequiredService<ITabularFile>().Read(new MemoryStream(bytes), TabularFormat.Xlsx, 1000);
        var row = table.Rows.Single(r => r[table.Headers.ToList().IndexOf("sku")] == sku);
        Assert.Equal(nameBn, row[table.Headers.ToList().IndexOf("name_bn")]);
        Assert.Equal("1250.5", row[table.Headers.ToList().IndexOf("regular_price")]);

        var report = await TestApi.ExpectAsync(await UploadAsync(token, bytes, "products.xlsx", "?mode=upsert"), HttpStatusCode.OK);
        Assert.True(report.GetProperty("committed").GetBoolean(), report.ToString());
        Assert.Equal(0, report.GetProperty("productsUpdated").GetInt32());
        Assert.Equal("Xlsx", report.GetProperty("format").GetString());
    }

    [Fact]
    public void Excel_files_written_by_other_tools_are_read_with_shared_strings_sparse_cells_and_numbers()
    {
        var tabular = factory.Services.GetRequiredService<ITabularFile>();
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new Text("name_en")), new SharedStringItem(new Text("category")), new SharedStringItem(new Text("regular_price")),
                new SharedStringItem(new Text("Tee, \"quoted\"")), new SharedStringItem(new Text("Clothing")));
            var sheetData = new SheetData(
                new Row(Cell("A1", 0), Cell("B1", 1), Cell("D1", 2)),                        // C1 is skipped: sparse row
                new Row(Cell("A2", 3), Cell("B2", 4), new Cell { CellReference = "D2", CellValue = new CellValue("1200") }));
            var sheet = workbook.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new Worksheet(sheetData);
            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheet), SheetId = 1, Name = "Data" }));
        }

        stream.Position = 0;
        var table = tabular.Read(stream, TabularFormat.Xlsx, 100);

        Assert.Equal(["name_en", "category", "", "regular_price"], table.Headers);
        Assert.Equal(["Tee, \"quoted\"", "Clothing", "", "1200"], Assert.Single(table.Rows));

        static Cell Cell(string reference, int sharedIndex) => new() { CellReference = reference, DataType = CellValues.SharedString, CellValue = new CellValue(sharedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
    }

    // ---- CSV details --------------------------------------------------------------------------------------

    [Fact]
    public void Csv_handles_quotes_newlines_semicolons_a_byte_order_mark_and_empty_lines()
    {
        var tabular = factory.Services.GetRequiredService<ITabularFile>();
        var text = "﻿a;b;c\r\n\"x;1\";\"line1\nline2\";\"say \"\"hi\"\"\"\r\n\r\n;;\r\nlast;;z";

        var table = tabular.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), TabularFormat.Csv, 100);

        Assert.Equal(["a", "b", "c"], table.Headers);
        Assert.Equal(["x;1", "line1\nline2", "say \"hi\""], table.Rows[0]);
        Assert.Equal(2, table.Rows.Count); // the empty line and the row of empty cells are skipped
        Assert.Equal(["last", "", "z"], table.Rows[1]);
    }

    [Fact]
    public void Csv_export_protects_text_that_looks_like_a_formula_but_not_numbers_and_import_removes_the_protection()
    {
        var tabular = factory.Services.GetRequiredService<ITabularFile>();
        var table = new TableData(["name_en", "regular_price"], [["=HYPERLINK(\"http://x\")", "-5"], ["a, b", "10"]], new HashSet<string> { "regular_price" });

        var bytes = tabular.Write(table, TabularFormat.Csv);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("'=HYPERLINK", text, StringComparison.Ordinal);
        Assert.Contains(",-5", text, StringComparison.Ordinal); // numeric column untouched
        Assert.Contains("\"a, b\"", text, StringComparison.Ordinal);
        var back = tabular.Read(new MemoryStream(bytes), TabularFormat.Csv, 100);
        Assert.Equal("'=HYPERLINK(\"http://x\")", back.Rows[0][0]); // the reader keeps raw text; the importer strips the apostrophe
    }

    [Fact]
    public async Task Text_that_was_protected_on_export_is_cleaned_when_it_is_imported_again()
    {
        var (token, _, _) = await AdminAndBranchAsync();
        var name = "Formula Tee " + U();

        var report = await ImportAsync(token, "name_en,category,regular_price,short_description\n" + $"{name},Clothing > T-shirts,100,'=1+1");

        Assert.True(report.GetProperty("committed").GetBoolean(), report.ToString());
        Assert.Equal("=1+1", (await ProductBySlugAsync(token, SlugOf(name))).GetProperty("shortDescription").GetString());
    }
}
