using System;
using System.Net;

internal static partial class Program
{
    private static void ConfigureNetworkSecurity()
    {
        try
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }
        catch (Exception ex)
        {
            Log("network_security_config_error " + ex.Message);
        }
    }

    private static string CallSaladBowlGrpcHttp2Empty(string method)
    {
        var path = "/salad.grpc.salad_bowl_widget.v1alpha.SaladBowlWidgetService/" + method;

        try
        {
            var port = GetSaladBowlGrpcPort();
            return CallGrpcHttp2UnaryEmpty("127.0.0.1", port, path);
        }
        catch (Exception ex)
        {
            return "error=" + ex.Message;
        }
    }









































}
