using System.Net.NetworkInformation;

internal sealed record AirDropStatus(string State, string Message, string[] Adapters)
{
    public static AirDropStatus Read()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(adapter => adapter.Description)
                .Distinct()
                .Order()
                .ToArray();

            return adapters.Length == 0
                ? new("no-adapter", "AirDrop nicht verfügbar: Windows meldet keinen WLAN-Adapter.", adapters)
                : new("unverified", "AirDrop nicht verfügbar: Das AWDL-Modul fehlt. Ob der WLAN-Adapter mit seinem Windows-Treiber AWDL unterstützt, ist ungeprüft.", adapters);
        }
        catch (NetworkInformationException)
        {
            return new("list-failed", "Die WLAN-Adapter konnten nicht ausgelesen werden.", []);
        }
    }
}