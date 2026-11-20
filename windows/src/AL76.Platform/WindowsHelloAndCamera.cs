using System.Runtime.InteropServices;
using AL76.Core;
using AL76.Core.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Security.Credentials.UI;
using Windows.Storage.Streams;

namespace AL76.Platform;

/// <summary>
/// Windows Hello after the spoken "yes" (Mac: OwnerCheck with Touch ID): face, fingerprint or PIN, in Windows' own
/// window; AL-76 gets only yes or no. A PC without Windows Hello set up answers null: the "yes" is enough there.
/// </summary>
public sealed class WindowsHello(Func<IntPtr> owner) : IOwnerCheck
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    /// <summary>The window in front: Windows Hello's dialog opens over it instead of behind.</summary>
    public static IntPtr Foreground() => GetForegroundWindow();

    public async Task<bool?> VerifyAsync(string reason, CancellationToken ct)
    {
        try
        {
            var available = await UserConsentVerifier.CheckAvailabilityAsync();
            if (available != UserConsentVerifierAvailability.Available)
            {
                Log.Info($"Windows Hello: {available}");
                return available is UserConsentVerifierAvailability.DeviceNotPresent or UserConsentVerifierAvailability.NotConfiguredForUser
                                 or UserConsentVerifierAvailability.DisabledByPolicy ? null : false;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            IntPtr hwnd = owner();
            var operation = hwnd != IntPtr.Zero
                ? UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, reason)
                : UserConsentVerifier.RequestVerificationAsync(reason);
            using (limit.Token.Register(() => { try { operation.Cancel(); } catch (InvalidOperationException) { } }))
            {
                var result = await operation;
                Log.Info($"Windows Hello: {result}");
                return result == UserConsentVerificationResult.Verified;
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            Log.Info($"Windows Hello: {e.Message}");
            return false;
        }
    }
}

/// <summary>
/// A photo from the PC's camera for look_at_camera (Mac: VisionActions.capturePhoto): the camera is opened, one frame
/// taken as JPEG and the camera closed at once. Nothing is saved to disk. Windows asks the camera permission itself.
/// </summary>
public static class WindowsCamera
{
    public static async Task<(byte[] jpeg, string label)?> PhotoAsync(CancellationToken ct)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            await Task.Delay(700, ct);   // a moment for the exposure to settle
            using var stream = new InMemoryRandomAccessStream();
            await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
            // Scaled to at most 1600 px on the long side, like the screenshots.
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            double scale = Math.Min(1, 1600.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            encoder.BitmapTransform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
            encoder.BitmapTransform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
            string name = capture.MediaCaptureSettings.VideoDeviceId is { Length: > 0 } id
                ? (await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(id)).Name : "fotocamera";
            Log.Audit("fotocamera usata", name);
            return (bytes, name);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Info($"fotocamera: {e.Message}");
            return null;
        }
        finally { capture.Dispose(); }
    }
}
