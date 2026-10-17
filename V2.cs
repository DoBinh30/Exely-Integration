using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace ExelyPmsConnectDemo;

internal static class Program
{
    private const string Endpoint = "https://pmsconnect.test.hopenapi.com/api/PMSConnect.svc?HotelCode=501661";
    private const string Username = "";
    private const string Password = "";
    private const string HotelCode = "501661";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("EXELY_ENDPOINT") ?? Endpoint;
            var username = Environment.GetEnvironmentVariable("EXELY_USERNAME") ?? Username;
            var password = Environment.GetEnvironmentVariable("EXELY_PASSWORD") ?? Password;
            var hotelCode = Environment.GetEnvironmentVariable("EXELY_HOTEL_CODE") ?? HotelCode;

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var exely = new ExelyPmsConnectClient(
                httpClient,
                new Uri(endpoint, UriKind.Absolute),
                username,
                password);

            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "read";
            if (command == "read")
            {
                // List<ExelyBooking>: gán trực tiếp vào grid hoặc map vào câu INSERT.
                // Ví dụ WinForms: dataGridView.DataSource = bookings;
                var bookings = await exely.GetUndeliveredBookingsAsync(hotelCode);
                Console.WriteLine(JsonSerializer.Serialize(
                    bookings,
                    new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            if (command == "confirm")
            {
                if (args.Length is < 5 or > 7)
                {
                    Console.Error.WriteLine(
                        "Cú pháp: confirm <EXELY_ID> <PMS_CREATE_TIME> <LAST_MODIFY_TIME> " +
                        "<PMS_ID> [ID_CONTEXT] [ROOM_STAYS_CSV]");
                    return 2;
                }

                var idContext = args.Length >= 6 && !string.IsNullOrWhiteSpace(args[5])
                    ? args[5]
                    : null;
                var roomStayIndexes = args.Length == 7
                    ? args[6].Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : Array.Empty<string>();

                var rawResponse = await exely.ConfirmBookingAsync(
                    hotelCode,
                    args[1],
                    args[2],
                    args[3],
                    args[4],
                    idContext,
                    roomStayIndexes);
                Console.WriteLine(rawResponse);
                return 0;
            }

            Console.Error.WriteLine("Lệnh hợp lệ: read hoặc confirm.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

/// <summary>
/// V2 giữ nguyên hai API: đọc booking trả List<ExelyBooking> để bind grid/lưu DB;
/// xác nhận booking vẫn trả raw response như Program.cs.
/// </summary>
public sealed class ExelyPmsConnectClient
{
    private const string ProtocolVersion = "1.18";
    private const string ReadSoapAction =
        "https://www.hopenapi.com/Api/PMSConnect/HotelReadReservationRQ";
    private const string ConfirmSoapAction =
        "https://www.hopenapi.com/Api/PMSConnect/NotifReportRQRequest";

    private static readonly XNamespace Soap =
        "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Security =
        "https://www.hopenapi.com/Api/PMSConnect";
    private static readonly XNamespace Ota =
        "http://www.opentravel.org/OTA/2003/05";

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _username;
    private readonly string _password;

    public ExelyPmsConnectClient(
        HttpClient httpClient,
        Uri endpoint,
        string username,
        string password)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _username = username;
        _password = password;
    }

    /// <summary>Lấy danh sách booking chưa được PMS xác nhận.</summary>
    public async Task<List<ExelyBooking>> GetUndeliveredBookingsAsync(
        string hotelCode,
        CancellationToken cancellationToken = default)
    {
        var otaRequest = new XElement(
            Ota + "OTA_ReadRQ",
            new XAttribute("Version", ProtocolVersion),
            new XElement(
                Ota + "ReadRequests",
                new XElement(
                    Ota + "HotelReadRequest",
                    new XAttribute("HotelCode", hotelCode),
                    new XElement(
                        Ota + "SelectionCriteria",
                        new XAttribute("SelectionType", "Undelivered")))));

        var rawResponse = await PostSoapAsync(ReadSoapAction, otaRequest, cancellationToken);
        return ParseBookings(rawResponse, hotelCode);
    }

    /// <summary>Xác nhận booking đã được tạo thành công trong PMS.</summary>
    public async Task<string> ConfirmBookingAsync(
        string hotelCode,
        string channelBookingId,
        string pmsCreateDateTime,
        string lastModifyDateTime,
        string pmsBookingId,
        string? idContext = null,
        IReadOnlyCollection<string>? roomStayIndexNumbers = null,
        CancellationToken cancellationToken = default)
    {
        var uniqueId = new XElement(
            Ota + "UniqueID",
            new XAttribute("Type", "14"),
            new XAttribute("ID", channelBookingId));
        if (!string.IsNullOrWhiteSpace(idContext))
        {
            uniqueId.Add(new XAttribute("ID_Context", idContext));
        }

        XElement? roomStays = null;
        if (roomStayIndexNumbers is { Count: > 0 })
        {
            roomStays = new XElement(
                Ota + "RoomStays",
                roomStayIndexNumbers.Select(index =>
                    new XElement(
                        Ota + "RoomStay",
                        new XAttribute("IndexNumber", index))));
        }

        var reservation = new XElement(
            Ota + "HotelReservation",
            new XAttribute("CreateDateTime", pmsCreateDateTime),
            new XAttribute("LastModifyDateTime", lastModifyDateTime),
            new XAttribute("ResStatus", "Reserved"),
            uniqueId,
            roomStays,
            new XElement(
                Ota + "ResGlobalInfo",
                new XElement(
                    Ota + "HotelReservationIDs",
                    new XElement(
                        Ota + "HotelReservationID",
                        new XAttribute("ResID_Type", "14"),
                        new XAttribute("ResID_Value", pmsBookingId)))));

        var otaRequest = new XElement(
            Ota + "OTA_NotifReportRQ",
            new XAttribute("Version", ProtocolVersion),
            new XAttribute("EchoToken", Guid.NewGuid().ToString("N")),
            new XElement(Ota + "Success"),
            new XElement(
                Ota + "NotifDetails",
                new XAttribute("HotelCode", hotelCode),
                new XElement(
                    Ota + "HotelNotifReport",
                    new XElement(Ota + "HotelReservations", reservation))));

        return await PostSoapAsync(ConfirmSoapAction, otaRequest, cancellationToken);
    }


    // Mỗi HotelReservation là một dòng ExelyBooking, không phải cả response là một dòng.
    private static List<ExelyBooking> ParseBookings(string rawResponse, string hotelCode)
    {
        var document = XDocument.Parse(rawResponse, LoadOptions.PreserveWhitespace);
        var body = document.Root?.Element(Soap + "Body");
        if (body?.Element(Soap + "Fault") is XElement fault)
        {
            throw new InvalidOperationException(
                "Exely SOAP Fault: " +
                fault.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "faultstring")?.Value);
        }

        var response = body?.Element(Ota + "OTA_ResRetrieveRS")
            ?? throw new InvalidOperationException("Response không có OTA_ResRetrieveRS.");
        if (response.Element(Ota + "Errors") is XElement errors)
        {
            throw new InvalidOperationException(
                "Exely: " + string.Join("; ", errors.Elements(Ota + "Error")
                    .Select(error => $"[{(string?)error.Attribute("Code")}] {error.Value}")));
        }

        if (response.Element(Ota + "Success") is null)
        {
            throw new InvalidOperationException("Response Exely chưa báo Success.");
        }

        var receivedAt = DateTime.Now;
        var bookings = new List<ExelyBooking>();
        var reservations = response.Element(Ota + "ReservationsList")?
            .Elements(Ota + "HotelReservation") ?? Enumerable.Empty<XElement>();

        foreach (var reservation in reservations)
        {
            var identifiers = reservation.Elements(Ota + "UniqueID")
                .Where(element => (string?)element.Attribute("Type") == "14")
                .ToArray();
            var identifier = identifiers.FirstOrDefault(element =>
                    string.IsNullOrWhiteSpace((string?)element.Attribute("ID_Context")))
                ?? identifiers.FirstOrDefault(element =>
                    (string?)element.Attribute("ID_Context") == "PMSConnect");
            var bookingId = TextOrNull((string?)identifier?.Attribute("ID"))
                ?? throw new InvalidOperationException(
                    "Booking thiếu UniqueID Type=14 thuộc Exely.");

            var globalInfo = reservation.Element(Ota + "ResGlobalInfo");
            var total = globalInfo?.Element(Ota + "Total");

            // Người đặt/người thanh toán. Chỉ dùng khách lưu trú nếu không có Customer này.
            var customer = globalInfo?.Element(Ota + "Profiles")?
                .Descendants(Ota + "Customer").FirstOrDefault()
                ?? reservation.Element(Ota + "ResGuests")?
                    .Descendants(Ota + "Customer").FirstOrDefault();
            var personName = customer?.Element(Ota + "PersonName");

            var channels = reservation.Element(Ota + "POS")?
                .Elements(Ota + "Source")
                .Elements(Ota + "BookingChannel").ToArray()
                ?? Array.Empty<XElement>();
            var channel = channels.FirstOrDefault(element =>
                (bool?)element.Attribute("Primary") == true) ?? channels.FirstOrDefault();
            var channelCompany = channel?.Element(Ota + "CompanyName");
            var webSource = channel?.Element(Ota + "TPA_Extensions")?
                .Element(Ota + "BookingWebSource");

            var stays = reservation.Element(Ota + "RoomStays")?
                .Elements(Ota + "RoomStay").ToArray() ?? Array.Empty<XElement>();
            var startDates = stays
                .Select(stay => ReadStayDate(stay.Element(Ota + "TimeSpan")?.Attribute("Start")))
                .Where(date => date.HasValue).Select(date => date.GetValueOrDefault()).ToArray();
            var endDates = stays
                .Select(stay => ReadStayDate(stay.Element(Ota + "TimeSpan")?.Attribute("End")))
                .Where(date => date.HasValue).Select(date => date.GetValueOrDefault()).ToArray();
            DateTime? checkin = startDates.Length > 0 ? startDates.Min() : null;
            DateTime? checkout = endDates.Length > 0 ? endDates.Max() : null;

            var guestCounts = stays.SelectMany(stay =>
                    stay.Element(Ota + "GuestCounts")?.Elements(Ota + "GuestCount")
                    ?? Enumerable.Empty<XElement>())
                .Select(count => (int?)count.Attribute("Count"))
                .Where(count => count.HasValue)
                .Select(count => count.GetValueOrDefault()).ToArray();

            var paymentComment = globalInfo?.Element(Ota + "Guarantee")?
                .Element(Ota + "Comments")?.Elements(Ota + "Comment")
                .FirstOrDefault(comment =>
                    (string?)comment.Attribute("Name") == "PaymentMethodName");
            var guestComments = globalInfo?.Element(Ota + "Comments")?
                .Elements(Ota + "Comment")
                .Where(comment =>
                    string.IsNullOrWhiteSpace((string?)comment.Attribute("Name")))
                .Select(comment => TextOrNull((string?)comment.Element(Ota + "Text")))
                .Where(text => text is not null).Cast<string>().ToArray()
                ?? Array.Empty<string>();

            bookings.Add(new ExelyBooking
            {
                HotelCode = hotelCode,
                ExelyBookingID = bookingId,
                RawXml = reservation.ToString(SaveOptions.DisableFormatting),
                ReceivedAt = receivedAt,
                BookingChannel = TextOrNull((string?)channel?.Attribute("Type")),
                BookingWebSource = TextOrNull((string?)webSource?.Attribute("Code"))
                    ?? TextOrNull((string?)webSource?.Attribute("Url")),
                CheckinDate = checkin,
                CheckoutDate = checkout,
                Duration = checkin.HasValue && checkout.HasValue
                    ? checked((short)(checkout.Value - checkin.Value).Days)
                    : null,
                GuestCount = guestCounts.Length > 0 ? guestCounts.Sum() : null,
                AmountBeforeTax = (decimal?)total?.Attribute("AmountBeforeTax"),
                AmountAfterTax = (decimal?)total?.Attribute("AmountAfterTax"),
                CurrencyCode = TextOrNull((string?)total?.Attribute("CurrencyCode")),
                PaymentMethod = TextOrNull((string?)paymentComment?.Element(Ota + "Text")),
                Gender = TextOrNull((string?)customer?.Attribute("Gender")),
                GivenName = TextOrNull((string?)personName?.Element(Ota + "GivenName")),
                MiddleName = TextOrNull((string?)personName?.Element(Ota + "MiddleName")),
                Surname = TextOrNull((string?)personName?.Element(Ota + "Surname")),
                PhoneNumber = TextOrNull(
                    (string?)customer?.Element(Ota + "Telephone")?.Attribute("PhoneNumber")),
                Email = TextOrNull((string?)customer?.Element(Ota + "Email")),
                CountryCode = TextOrNull(
                    (string?)customer?.Element(Ota + "CitizenCountryName")?.Attribute("Code")),
                GuestComment = guestComments.Length > 0
                    ? string.Join(Environment.NewLine, guestComments)
                    : null,

                // Metadata phục vụ grid/xác nhận; không có trong bảng SQL hiện tại.
                LastModifyDateTime = (string?)reservation.Attribute("LastModifyDateTime"),
                IdContext = (string?)identifier?.Attribute("ID_Context"),
                ResStatus = (string?)reservation.Attribute("ResStatus"),
                BookingChannelCode = TextOrNull((string?)channelCompany?.Attribute("Code")),
                BookingChannelName = TextOrNull((string?)channelCompany),
                RoomStayIndexNumbers = stays
                    .Select(stay => TextOrNull((string?)stay.Attribute("IndexNumber")))
                    .Where(index => index is not null).Cast<string>().ToArray()
            });
        }

        return bookings;
    }

    private static string? TextOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ReadStayDate(XAttribute? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute?.Value))
        {
            return null;
        }

        // Giữ ngày theo giờ khách sạn trong XML; không đổi ngày theo múi giờ máy chạy.
        return DateTime.ParseExact(
            attribute.Value.Trim().Split('T')[0],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
    }

    private async Task<string> PostSoapAsync(
        string soapAction,
        XElement otaRequest,
        CancellationToken cancellationToken)
    {
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
                new XElement(
                    Soap + "Header",
                    new XAttribute(XNamespace.Xmlns + "pms", Security.NamespaceName),
                    new XElement(
                        Security + "Security",
                        new XAttribute("Username", _username),
                        new XAttribute("Password", _password))),
                new XElement(Soap + "Body", otaRequest)));

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");
        request.Content = new StringContent(
            envelope.ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8,
            "text/xml");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}


/// <summary>
/// Các thuộc tính map với bảng ExelyBooking được cung cấp.
/// ID do DB tự tăng; ReceivedAt là giờ máy tiếp nhận.
/// ConfirmedAt, SavedAt, PMSBookingID chỉ cập nhật sau khi xử lý tương ứng thành công.
/// </summary>
public sealed class ExelyBooking
{
    public int ID { get; set; }
    public string HotelCode { get; set; } = string.Empty;
    public string ExelyBookingID { get; set; } = string.Empty;

    // Giữ XML của riêng booking; ẩn khỏi grid tự sinh cột và phần JSON preview.
    [Browsable(false)]
    [JsonIgnore]
    public string RawXml { get; set; } = string.Empty;

    public DateTime ReceivedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? SavedAt { get; set; }
    public string? PMSBookingID { get; set; }
    public string? BookingChannel { get; set; }

    // BookingWebSource@Code; nếu rỗng thì lấy @Url.
    public string? BookingWebSource { get; set; }

    // Nhiều RoomStay: ngày vào sớm nhất, ngày ra muộn nhất; Duration là độ dài khoảng này.
    public DateTime? CheckinDate { get; set; }
    public DateTime? CheckoutDate { get; set; }
    public short? Duration { get; set; }
    public int? GuestCount { get; set; }
    public decimal? AmountBeforeTax { get; set; }
    public decimal? AmountAfterTax { get; set; }
    public string? CurrencyCode { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Gender { get; set; }
    public string? GivenName { get; set; }
    public string? MiddleName { get; set; }
    public string? Surname { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? CountryCode { get; set; }
    public string? GuestComment { get; set; }

    // Các trường dưới đây chưa có trong SQL đã gửi: không đưa vào INSERT của bảng hiện tại.
    // NotMapped dùng được với EF; nếu dùng Dapper/ADO.NET, chỉ INSERT 25 cột ngoài ID ở trên.
    [NotMapped]
    public string? LastModifyDateTime { get; set; }

    [NotMapped]
    public string? IdContext { get; set; }

    [NotMapped]
    public string? ResStatus { get; set; }

    [NotMapped]
    public string? BookingChannelCode { get; set; }

    [NotMapped]
    public string? BookingChannelName { get; set; }

    [NotMapped]
    [Browsable(false)]
    public string[] RoomStayIndexNumbers { get; set; } = Array.Empty<string>();
}

