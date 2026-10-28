using WgSwitch.Services;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace WgSwitch.Tests;

public class QrTests
{
    /// <summary>Рисует код в серый растр с полями и читает его независимым декодером ZXing.</summary>
    private static string Decode(QrCode qr)
    {
        const int scale = 4, quiet = 4;
        int n = (qr.Size + quiet * 2) * scale;
        var pixels = new byte[n * n];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr[x, y])
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            pixels[((y + quiet) * scale + dy) * n + (x + quiet) * scale + dx] = 0;

        var source = new RGBLuminanceSource(pixels, n, n, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)),
            new Dictionary<DecodeHintType, object> { [DecodeHintType.CHARACTER_SET] = "UTF-8" });
        Assert.NotNull(result);
        return result.Text;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(260)]
    [InlineData(330)]
    [InlineData(600)]
    public void Decodes_back(int length)
    {
        var text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('A' + i % 26)));
        Assert.Equal(text, Decode(QrCode.Encode(text)));
    }

    [Fact]
    public void Wireguard_config_round_trips()
    {
        var conf = "# WgSwitch name=home ping=10.8.0.1\n[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\n" +
                   "Address = 10.8.0.3/32\n\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\n" +
                   "AllowedIPs = 10.8.0.0/24\nEndpoint = vpn.example.com:51820\nPersistentKeepalive = 25\n";
        Assert.Equal(conf, Decode(QrCode.Encode(conf)));
    }

    [Fact]
    public void Utf8_text() => Assert.Equal("Привет, телефон", Decode(QrCode.Encode("Привет, телефон")));
}
