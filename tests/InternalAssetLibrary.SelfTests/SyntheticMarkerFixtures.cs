using System.Text;
using InternalAssetLibrary.Core;

internal static class SyntheticMarkerFixtures
{
    // 原创合成夹具；不分发团队录制文件、CSV或其中的个人路径。
    public static byte[] Bytes(string name)
    {
        var body = name == "空白.csv" ? "1,,,,,,,,\r\n" :
            "1,00:00:01.000,,synthetic.mp4,,2026-01-01 00:00:00,02:20:41,3,标记 1\r\n" +
            "2,00:01:02.000,,,,,,,标记 2\r\n" +
            "3,01:25:34.554,,,,,,,标记 3\r\n";
        return [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(MarkersCsv.Header + "\r\n" + body)];
    }
}
