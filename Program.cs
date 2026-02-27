using System;
using System.IO;
using System.Net;
using System.Text;

namespace SoapClientDotNet35
{
    class SoapClientSimple
    {
        private readonly string _url;
        private readonly string _username;
        private readonly string _password;
        private readonly string _otaLink;
        private readonly string _hotelCode;

        public SoapClientSimple(string url, string username, string password, string otaLink, string hotelCode)
        {
            _url = url;
            _username = username;
            _password = password;
            _otaLink = otaLink;
            _hotelCode = hotelCode;
        }

        private string EscapeXml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private string SendSoap(string soapAction, string soapEnvelope)
        {
            byte[] data = Encoding.UTF8.GetBytes(soapEnvelope);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_url);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=utf-8";
            req.ContentLength = data.Length;
            if (!string.IsNullOrEmpty(soapAction))
            {
                // Some SOAP servers expect SOAPAction header (SOAP 1.1)
                req.Headers.Add("SOAPAction", soapAction);
            }

            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(data, 0, data.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream respStream = resp.GetResponseStream())
                using (StreamReader reader = new StreamReader(respStream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var errResp = (HttpWebResponse)wex.Response)
                    using (var errStream = errResp.GetResponseStream())
                    using (var errReader = new StreamReader(errStream ?? Stream.Null, Encoding.UTF8))
                    {
                        string err = errReader.ReadToEnd();
                        return "HTTP ERROR: " + err;
                    }
                }
                return "WebException: " + wex.Message;
            }
            catch (Exception ex)
            {
                return "Exception: " + ex.Message;
            }
        }

        public string GetBookings()
        {
            string soapAction = "https://www.hopenapi.com/Api/PMSConnect/HotelReadReservationRQ";
            string xml =
                @"<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                <soap:Header xmlns=""https://www.hopenapi.com/Api/PMSConnect"">
                    <Security Username=""{0}"" Password=""{1}"" />
                </soap:Header>
                <soap:Body>
                    <OTA_ReadRQ xmlns=""{2}"" Version=""1.17"">
                    <ReadRequests>
                        <HotelReadRequest HotelCode=""{3}"">
                        <SelectionCriteria SelectionType=""Undelivered""/>
                        </HotelReadRequest>
                    </ReadRequests>
                    </OTA_ReadRQ>
                </soap:Body>
                </soap:Envelope>";
            xml = string.Format(xml, EscapeXml(_username), EscapeXml(_password), EscapeXml(_otaLink), EscapeXml(_hotelCode));
            return SendSoap(soapAction, xml);
        }

        public string ConfirmBooking(string reservationId, string createdAt, string updatedAt)
        {
            string soapAction = "https://www.hopenapi.com/Api/PMSConnect/NotifReportRQRequest";
            string xml =
                @"<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                <soap:Header xmlns=""https://www.hopenapi.com/Api/PMSConnect"">
                    <Security Username=""{0}"" Password=""{1}"" />
                </soap:Header>
                <soap:Body>
                    <OTA_NotifReportRQ xmlns=""{2}"" Version=""1.17"">
                    <Success />
                    <NotifDetails HotelCode=""{3}"">
                        <HotelNotifReport>
                        <HotelReservations>
                            <HotelReservation CreateDateTime=""{4}"" LastModifyDateTime=""{5}"" ResStatus=""Reserved"">
                            <UniqueID Type=""14"" ID=""{6}"" />
                            <ResGlobalInfo>
                                <HotelReservationIDs>
                                <HotelReservationID ResID_Type=""14"" ResID_Value=""{7}""/>
                                </HotelReservationIDs>
                            </ResGlobalInfo>
                            </HotelReservation>
                        </HotelReservations>
                        </HotelNotifReport>
                    </NotifDetails>
                    </OTA_NotifReportRQ>
                </soap:Body>
                </soap:Envelope>";
            string randomRoomNumber = new Random().Next(1, 9999).ToString();
            xml = string.Format(xml,
                EscapeXml(_username),
                EscapeXml(_password),
                EscapeXml(_otaLink),
                EscapeXml(_hotelCode),
                EscapeXml(createdAt),
                EscapeXml(updatedAt),
                EscapeXml(reservationId),
                EscapeXml(randomRoomNumber)
            );
            return SendSoap(soapAction, xml);
        }

        public string CancelBooking(string reservationId, string reason, string amountStr)
        {
            string soapAction = "https://www.hopenapi.com/Api/PMSConnect/CancelRQ";
            string xml =
                @"<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                <soap:Header xmlns=""https://www.hopenapi.com/Api/PMSConnect"">
                    <Security Username=""{0}"" Password=""{1}"" />
                </soap:Header>
                <soap:Body>
                    <OTA_CancelRQ xmlns=""{2}"" Version=""1.17"">
                    <UniqueID ID_Context=""PMSConnect"" ID=""{3}"" Reason=""{4}""/>
                    <CancellationOverrides>
                        <CancellationOverride CurrencyCode=""VND"" Amount=""{5}"" />
                    </CancellationOverrides>
                    </OTA_CancelRQ>
                </soap:Body>
                </soap:Envelope>";
            xml = string.Format(xml,
                EscapeXml(_username),
                EscapeXml(_password),
                EscapeXml(_otaLink),
                EscapeXml(reservationId),
                EscapeXml(reason),
                EscapeXml(amountStr)
            );
            return SendSoap(soapAction, xml);
        }
    }

    class Program
    {
        const string API = "a";
        const string USERNAME = "b";
        const string PASSWORD = "c";
        const string OTA_LINK = "d";
        const string HOTEL_CODE = "e";

        static void Main(string[] args)
        {
            SoapClientSimple client = new SoapClientSimple(API, USERNAME, PASSWORD, OTA_LINK, HOTEL_CODE);

            Console.WriteLine("---- GET BOOKINGS ----");
            string getResp = client.GetBookings();
            Console.WriteLine(getResp);
            Console.WriteLine();

            // Console.WriteLine("---- CONFIRM BOOKING ----");
            // string sampleReservationId = "20260305-501661-1200384485";
            // string createdAt = DateTime.UtcNow.ToString("2026-02-27T19:00:27.053"); // 2026-02-27T... dạng ISO
            // string updatedAt = DateTime.UtcNow.ToString("2026-02-27T19:00:38.073");
            // string confirmResp = client.ConfirmBooking(sampleReservationId, createdAt, updatedAt);
            // Console.WriteLine(confirmResp);
            // Console.WriteLine();

            // Console.WriteLine("---- CANCEL BOOKING ----");
            // string cancelResp = client.CancelBooking(sampleReservationId, "We are close", "10000");
            // Console.WriteLine(cancelResp);
            // Console.WriteLine();

            // Console.WriteLine("Hoàn tất. Nhấn Enter để thoát.");
            // Console.ReadLine();
        }
    }
}
