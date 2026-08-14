using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("remote", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 RealseeExporter/1.0");
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/export", async (ExportRequest request, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!TryParsePackageNo(request.Url, out var pkgNo, out var error))
        return Results.BadRequest(new { message = error });

    var client = factory.CreateClient("remote");
    var workUrl = $"https://gateway.ikongjian.com/rsvr-api/api/v1/rs_vr/work_json?pkgNo={Uri.EscapeDataString(pkgNo)}&type=1";

    WorkData work;
    try
    {
        using var resp = await client.GetAsync(workUrl, ct);
        resp.EnsureSuccessStatusCode();
        var outerText = await resp.Content.ReadAsStringAsync(ct);
        using var outer = JsonDocument.Parse(outerText);
        if (!outer.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
            return Results.BadRequest(new { message = "如视接口返回失败。" });

        var dataText = outer.RootElement.GetProperty("data").GetString();
        if (string.IsNullOrWhiteSpace(dataText))
            return Results.BadRequest(new { message = "如视接口没有返回项目数据。" });

        work = ParseWorkData(dataText);
    }
    catch (Exception ex)
    {
        return Results.Problem($"读取项目数据失败：{ex.Message}");
    }

    if (!IsAllowedAssetBase(work.BaseUrl))
        return Results.BadRequest(new { message = "资源地址不在允许的如视/阿里云域名范围内。" });

    if (work.Panoramas.Count == 0)
        return Results.BadRequest(new { message = "没有发现全景拍摄点。" });

    try
    {
        await using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = pkgNo;
            var manifest = new List<object>();
            using var gate = new SemaphoreSlim(8);
            var jobs = new List<Task<DownloadedAsset>>();

            foreach (var pano in work.Panoramas.OrderBy(x => x.Index))
            {
                var point = $"point_{pano.Index}";
                manifest.Add(new { index = pano.Index, path = point });

                foreach (var face in pano.Faces)
                {
                    var faceName = face.Key;
                    var rel = face.Value;
                    jobs.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(ct);
                        try
                        {
                            var assetUrl = new Uri(new Uri(work.BaseUrl), rel);
                            if (!IsAllowedAssetHost(assetUrl.Host))
                                throw new InvalidOperationException($"非法资源域名：{assetUrl.Host}");

                            using var imgResp = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                            imgResp.EnsureSuccessStatusCode();
                            var bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
                            return new DownloadedAsset($"{root}/{point}/{pano.Index}_{faceName}.jpg", bytes);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, ct));
                }
            }

            var assets = await Task.WhenAll(jobs);
            foreach (var asset in assets)
            {
                var entry = zip.CreateEntry(asset.Path, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(asset.Bytes);
            }

            var threeJs = await DownloadThreeJs(client, ct);
            AddText(zip, $"{root}/viewer/three.min.js", threeJs);
            AddText(zip, $"{root}/index.html", BuildViewerHtml(pkgNo, work.Panoramas.Select(p => p.Index).OrderBy(x => x).ToArray()));
            AddText(zip, $"{root}/manifest.json", JsonSerializer.Serialize(new
            {
                packageNo = pkgNo,
                source = request.Url,
                points = manifest
            }, new JsonSerializerOptions { WriteIndented = true }));
            AddText(zip, $"{root}/README.txt", BuildPackageReadme(pkgNo));
        }

        ms.Position = 0;
        return Results.File(ms.ToArray(), "application/zip", $"{pkgNo}.zip");
    }
    catch (Exception ex)
    {
        return Results.Problem($"下载或打包失败：{ex.Message}");
    }
});

app.Run();

static bool TryParsePackageNo(string? input, out string pkgNo, out string error)
{
    pkgNo = "";
    error = "";
    if (string.IsNullOrWhiteSpace(input))
    {
        error = "请输入分享链接。";
        return false;
    }

    if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
    {
        error = "分享链接必须是 HTTPS URL。";
        return false;
    }

    if (!uri.Host.Equals("realsee.ikongjian.com", StringComparison.OrdinalIgnoreCase))
    {
        error = "目前只接受 realsee.ikongjian.com 分享链接。";
        return false;
    }

    var match = Regex.Match(uri.AbsolutePath, @"/vr/(?<pkg>PK\d+)", RegexOptions.IgnoreCase);
    if (!match.Success)
    {
        error = "链接中没有找到 PK 项目号。";
        return false;
    }

    pkgNo = match.Groups["pkg"].Value.ToUpperInvariant();
    return true;
}

static WorkData ParseWorkData(string json)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var baseUrl = root.GetProperty("base_url").GetString() ?? throw new InvalidOperationException("缺少 base_url");
    var list = root.GetProperty("panorama").GetProperty("list");
    var panos = new List<Pano>();

    foreach (var item in list.EnumerateArray())
    {
        var idx = item.GetProperty("index").GetInt32();
        panos.Add(new Pano(idx, new Dictionary<string, string>
        {
            ["r"] = item.GetProperty("right").GetString()!,
            ["l"] = item.GetProperty("left").GetString()!,
            ["u"] = item.GetProperty("up").GetString()!,
            ["d"] = item.GetProperty("down").GetString()!,
            ["f"] = item.GetProperty("front").GetString()!,
            ["b"] = item.GetProperty("back").GetString()!
        }));
    }

    return new WorkData(baseUrl, panos);
}

static bool IsAllowedAssetBase(string value)
{
    return Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && IsAllowedAssetHost(uri.Host);
}

static bool IsAllowedAssetHost(string host)
{
    return host.Equals("ikongjian.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ikongjian.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("aliyuncs.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase);
}

static async Task<string> DownloadThreeJs(HttpClient client, CancellationToken ct)
{
    const string url = "https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.min.js";
    return await client.GetStringAsync(url, ct);
}

static void AddText(ZipArchive zip, string path, string text)
{
    var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
    using var stream = entry.Open();
    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
    writer.Write(text);
}

static string BuildPackageReadme(string pkgNo)
{
    return $$"""
{{pkgNo}} 房屋全景浏览说明
==============================

一、打开前请注意

1. 请先把 ZIP 压缩包完整解压，再进行下面的操作。
2. 不要直接在压缩包里打开 index.html。
3. 不建议双击 index.html 直接打开。浏览器通常会阻止网页读取同一文件夹中的全景图片，
   可能出现黑屏、图片缺失或“加载失败”，这不代表文件已经损坏。

二、推荐方法：使用 Live Server 打开

如果电脑上已经安装 Visual Studio Code：

1. 打开 Visual Studio Code，选择“文件”→“打开文件夹”。
2. 选择解压得到的 {{pkgNo}} 文件夹。
3. 在左侧找到 index.html，右键选择“Open with Live Server”。
4. 浏览器会自动打开全景页面。

如果右键没有“Open with Live Server”：

1. 点击 Visual Studio Code 左侧的“扩展”图标。
2. 搜索 Live Server，安装后重新执行上面的步骤。

三、常见备用方法：电脑已经安装 Python 时

1. 打开解压后的 {{pkgNo}} 文件夹。
2. 在文件夹地址栏输入 cmd，然后按回车键。
3. 在出现的黑色窗口中粘贴下面这行内容并按回车键：

   python -m http.server 8000 --bind 127.0.0.1

4. 打开浏览器，在地址栏输入：

   http://127.0.0.1:8000/

5. 浏览结束后关闭黑色窗口即可。

四、仍然无法打开时

- 确认 ZIP 已完整解压，而不是在压缩包预览窗口中操作。
- 确认 index.html、viewer 文件夹和 point_ 开头的图片文件夹仍在同一目录中。
- 换用 Chrome、Edge 或 Firefox 的最新版本重试。
- 这些文件可以保存在本地；只有安装工具时可能需要联网，浏览全景本身不需要访问原网站。

安全提示：全景图片只能辅助判断水电管线位置。钻孔或拆改前，请使用专业设备再次确认，
不要仅凭图片直接施工。
""";
}

static string BuildViewerHtml(string pkgNo, int[] points)
{
    var pointJson = JsonSerializer.Serialize(points);
    return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{pkgNo}} 本地全景</title>
<style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#111;font-family:system-ui,sans-serif}#app{position:fixed;inset:0}#bar{position:fixed;z-index:2;left:16px;top:16px;padding:12px;background:#0009;color:#fff;border-radius:10px;backdrop-filter:blur(8px)}#pts{display:grid;grid-template-columns:repeat(4,minmax(56px,1fr));gap:6px;max-width:320px;max-height:60vh;overflow:auto;margin-top:8px;padding-right:4px}button{border:0;border-radius:6px;padding:7px 10px;background:#ffffff24;color:#fff;cursor:pointer}button.active{background:#ffffff55}.hint{font-size:12px;opacity:.7;margin-top:8px}
</style>
</head>
<body>
<div id="app"></div><div id="bar"><b>{{pkgNo}}</b><div class="hint">共 <span id="count"></span> 个点位</div><div id="pts"></div><div class="hint">拖动旋转 · 滚轮缩放 · 双击复位</div></div>
<script src="./viewer/three.min.js"></script>
<script>
const points={{pointJson}};
document.querySelector('#count').textContent=points.length;
const scene=new THREE.Scene();
const camera=new THREE.PerspectiveCamera(75,innerWidth/innerHeight,.1,1000);camera.position.z=.01;
const renderer=new THREE.WebGLRenderer({antialias:true});renderer.setPixelRatio(Math.min(devicePixelRatio,2));renderer.setSize(innerWidth,innerHeight);document.querySelector('#app').appendChild(renderer.domElement);
const loader=new THREE.CubeTextureLoader();let lon=0,lat=0,drag=false,sx=0,sy=0,slon=0,slat=0;
function urls(i){const b=`./point_${i}`;return [`${b}/${i}_r.jpg`,`${b}/${i}_l.jpg`,`${b}/${i}_u.jpg`,`${b}/${i}_d.jpg`,`${b}/${i}_f.jpg`,`${b}/${i}_b.jpg`]}
function load(i){loader.load(urls(i),t=>{scene.background?.dispose?.();scene.background=t;document.querySelectorAll('button').forEach(b=>b.classList.toggle('active',+b.dataset.i===i))},undefined,e=>alert(`point_${i} 加载失败`))}
for(const i of points){const b=document.createElement('button');b.textContent=`点 ${i}`;b.dataset.i=i;b.addEventListener('click',()=>load(i));document.querySelector('#pts').appendChild(b)}
renderer.domElement.addEventListener('pointerdown',e=>{drag=true;sx=e.clientX;sy=e.clientY;slon=lon;slat=lat;renderer.domElement.setPointerCapture(e.pointerId)});
renderer.domElement.addEventListener('pointermove',e=>{if(!drag)return;lon=slon+(e.clientX-sx)*.12;lat=Math.max(-85,Math.min(85,slat-(e.clientY-sy)*.12))});
renderer.domElement.addEventListener('pointerup',e=>{drag=false;renderer.domElement.releasePointerCapture(e.pointerId)});
renderer.domElement.addEventListener('wheel',e=>{e.preventDefault();camera.fov=Math.max(25,Math.min(100,camera.fov+e.deltaY*.025));camera.updateProjectionMatrix()},{passive:false});
renderer.domElement.addEventListener('dblclick',()=>{lon=lat=0;camera.fov=75;camera.updateProjectionMatrix()});
addEventListener('resize',()=>{camera.aspect=innerWidth/innerHeight;camera.updateProjectionMatrix();renderer.setSize(innerWidth,innerHeight)});
(function loop(){requestAnimationFrame(loop);const p=THREE.MathUtils.degToRad(90-lat),t=THREE.MathUtils.degToRad(lon);camera.lookAt(Math.sin(p)*Math.sin(t),Math.cos(p),Math.sin(p)*Math.cos(t));renderer.render(scene,camera)})();
load(points[0]);
</script>
</body></html>
""";
}

record ExportRequest(string Url);
record WorkData(string BaseUrl, List<Pano> Panoramas);
record Pano(int Index, Dictionary<string, string> Faces);
record DownloadedAsset(string Path, byte[] Bytes);
