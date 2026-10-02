using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Database;
using global::Android.OS;
using global::Android.Provider;
using DungeonRunners.Client;

namespace DungeonRunners.Android;

public static class ApkUpdates
{
    public const string Authority = "com.aardvarkland.dungeonrunners.updates";

    public static void Install(Activity activity, string apk)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) throw new IOException("Automatic APK updates require Android 9 or later. Use the repository download on this device.");
        var manager = activity.PackageManager!;
        var incoming = manager.GetPackageArchiveInfo(apk, PackageInfoFlags.SigningCertificates) ?? throw new IOException("Invalid APK package.");
        var current = manager.GetPackageInfo(activity.PackageName!, PackageInfoFlags.SigningCertificates)!;
        if (incoming.PackageName != current.PackageName || incoming.LongVersionCode < current.LongVersionCode)
            throw new IOException("APK identity or version does not match this application.");
        var expected = current.SigningInfo?.GetApkContentsSigners()?.Select(s => s.ToCharsString()).ToHashSet(StringComparer.Ordinal);
        var received = incoming.SigningInfo?.GetApkContentsSigners()?.Select(s => s.ToCharsString()).ToHashSet(StringComparer.Ordinal);
        if (expected is null || received is null || expected.Count == 0 || !expected.SetEquals(received)) throw new IOException("The APK signing certificate changed. The installed launcher was preserved.");
        RequestInstall(activity, apk, "launcher-update.apk");
    }

    public static void InstallRuntime(Activity activity, string apk)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) throw new IOException("The verified runtime requires Android 9 or later.");
        var incoming = activity.PackageManager!.GetPackageArchiveInfo(apk, PackageInfoFlags.SigningCertificates) ?? throw new IOException("Invalid runtime APK.");
        var signatures = incoming.SigningInfo?.GetApkContentsSigners();
        if (incoming.PackageName != AndroidRuntime.PackageId || signatures is null || signatures.Length != 1
            || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(signatures[0].ToByteArray()!)).ToLowerInvariant() != AndroidRuntime.Certificate)
            throw new IOException("The runtime APK identity or certificate changed.");
        RequestInstall(activity, apk, "runtime-install.apk");
    }

    private static void RequestInstall(Activity activity, string apk, string name)
    {
        var destination = System.IO.Path.Combine(activity.CacheDir!.CanonicalPath, name);
        if (System.IO.Path.GetFullPath(apk) != destination) File.Copy(apk, destination, true);
        if (!activity.PackageManager!.CanRequestPackageInstalls())
        {
            activity.GetPreferences(FileCreationMode.Private)!.Edit()!.PutString("pendingApk", name)!.Apply();
            activity.StartActivity(new Intent(Settings.ActionManageUnknownAppSources, global::Android.Net.Uri.Parse("package:" + activity.PackageName)));
            return;
        }
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(global::Android.Net.Uri.Parse("content://" + Authority + "/" + name), "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(intent);
    }

    public static void Resume(Activity activity)
    {
        var preferences = activity.GetPreferences(FileCreationMode.Private)!;
        var name = preferences.GetString("pendingApk", null);
        if (name is null || !activity.PackageManager!.CanRequestPackageInstalls()) return;
        preferences.Edit()!.Remove("pendingApk")!.Apply();
        var path = System.IO.Path.Combine(activity.CacheDir!.CanonicalPath, name);
        if (name == "launcher-update.apk") Install(activity, path);
        else if (name == "runtime-install.apk") InstallRuntime(activity, path);
        else throw new IOException("Invalid pending package.");
    }
}

[ContentProvider(new[] { ApkUpdates.Authority }, Exported = false, GrantUriPermissions = true)]
public sealed class ApkProvider : ContentProvider
{
    private string PathFor(global::Android.Net.Uri uri)
    {
        if (uri.Authority != ApkUpdates.Authority || uri.Path is not ("/launcher-update.apk" or "/runtime-install.apk")) throw new FileNotFoundException();
        return System.IO.Path.Combine(Context!.CacheDir!.CanonicalPath, uri.Path[1..]);
    }
    public override bool OnCreate() => true;
    public override string GetType(global::Android.Net.Uri uri) { PathFor(uri); return "application/vnd.android.package-archive"; }
    public override ParcelFileDescriptor? OpenFile(global::Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new FileNotFoundException();
        return ParcelFileDescriptor.Open(new Java.IO.File(PathFor(uri)), ParcelFileMode.ReadOnly);
    }
    public override ICursor? Query(global::Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        var file = new FileInfo(PathFor(uri));
        var columns = projection ?? new[] { IOpenableColumns.DisplayName, IOpenableColumns.Size };
        var cursor = new MatrixCursor(columns);
        var row = cursor.NewRow()!;
        foreach (var column in columns)
            row.Add(column == IOpenableColumns.DisplayName ? new Java.Lang.String(file.Name) : column == IOpenableColumns.Size ? Java.Lang.Long.ValueOf(file.Length) : null);
        return cursor;
    }
    public override global::Android.Net.Uri? Insert(global::Android.Net.Uri uri, ContentValues? values) => throw new NotSupportedException();
    public override int Delete(global::Android.Net.Uri uri, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
    public override int Update(global::Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
}
