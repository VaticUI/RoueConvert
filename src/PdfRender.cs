using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace RoueConvert
{
    // PDF pages -> images with the PDF renderer built into Windows (Windows.Data.Pdf).
    // WinRT is reached through reflection so the build needs no Windows SDK.
    internal static class PdfRender
    {
        static readonly Assembly Rt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");

        static Type W(string fullName, string winmd)
        {
            return Type.GetType(fullName + ", " + winmd + ", ContentType=WindowsRuntime", true);
        }

        static object Await(object op, Type resultType)
        {
            var ext = Rt.GetType("System.WindowsRuntimeSystemExtensions", true);
            MethodInfo m;
            if (resultType == null)
                m = ext.GetMethods().First(x => x.Name == "AsTask" && !x.IsGenericMethod && x.GetParameters().Length == 1
                                             && x.GetParameters()[0].ParameterType.Name == "IAsyncAction");
            else
                m = ext.GetMethods().First(x => x.Name == "AsTask" && x.IsGenericMethod && x.GetGenericArguments().Length == 1
                                             && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1")
                       .MakeGenericMethod(resultType);
            var task = (Task)m.Invoke(null, new[] { op });
            task.Wait();
            return resultType == null ? null : task.GetType().GetProperty("Result").GetValue(task);
        }

        public static List<string> ToImages(string src, string dir, string baseName, string target, Action<double> progress)
        {
            Type tFile = W("Windows.Storage.StorageFile", "Windows.Storage");
            Type tDoc = W("Windows.Data.Pdf.PdfDocument", "Windows.Data");
            Type tPage = W("Windows.Data.Pdf.PdfPage", "Windows.Data");
            Type tOpt = W("Windows.Data.Pdf.PdfPageRenderOptions", "Windows.Data");
            Type tMem = W("Windows.Storage.Streams.InMemoryRandomAccessStream", "Windows.Storage");
            Type tIn = W("Windows.Storage.Streams.IInputStream", "Windows.Storage");
            var asStream = Rt.GetType("System.IO.WindowsRuntimeStreamExtensions", true).GetMethod("AsStreamForRead", new[] { tIn });

            object file = Await(tFile.GetMethod("GetFileFromPathAsync").Invoke(null, new object[] { src }), tFile);
            object doc;
            try
            {
                var load = tDoc.GetMethods().First(x => x.Name == "LoadFromFileAsync" && x.GetParameters().Length == 1);
                doc = Await(load.Invoke(null, new[] { file }), tDoc);
            }
            catch (Exception) { throw new Exception("PDF illisible ou protégé par mot de passe"); }

            uint count = (uint)tDoc.GetProperty("PageCount").GetValue(doc);
            var outs = new List<string>();
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    BitmapSource img;
                    object page = tDoc.GetMethod("GetPage").Invoke(doc, new object[] { i });
                    try
                    {
                        object size = tPage.GetProperty("Size").GetValue(page);
                        double pw = (double)size.GetType().GetProperty("Width").GetValue(size);
                        double ph = (double)size.GetType().GetProperty("Height").GetValue(size);
                        // ~200 dpi, capped so huge pages stay reasonable.
                        double scale = Math.Min(200.0 / 96, 6000 / Math.Max(pw, ph));
                        object opt = Activator.CreateInstance(tOpt);
                        tOpt.GetProperty("DestinationWidth").SetValue(opt, (uint)Math.Max(1, Math.Round(pw * scale)));
                        tOpt.GetProperty("DestinationHeight").SetValue(opt, (uint)Math.Max(1, Math.Round(ph * scale)));
                        object ras = Activator.CreateInstance(tMem);
                        var render = tPage.GetMethods().First(x => x.Name == "RenderToStreamAsync" && x.GetParameters().Length == 2);
                        Await(render.Invoke(page, new[] { ras, opt }), null);
                        tMem.GetMethod("Seek").Invoke(ras, new object[] { 0UL });
                        using (var s = (Stream)asStream.Invoke(null, new[] { ras }))
                        {
                            var ms = new MemoryStream();
                            s.CopyTo(ms);
                            ms.Position = 0;
                            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                            img = dec.Frames[0];
                            img.Freeze();
                        }
                        var d = ras as IDisposable;
                        if (d != null) d.Dispose();
                    }
                    finally
                    {
                        var d = page as IDisposable;
                        if (d != null) d.Dispose();
                    }
                    string name = count == 1 ? baseName : baseName + " - page " + (i + 1);
                    string dst = Converter.UniquePath(dir, name, target);
                    ImageConv.Save(img, dst, target);
                    outs.Add(dst);
                    progress((i + 1.0) / count);
                }
            }
            catch
            {
                foreach (var o in outs) { try { File.Delete(o); } catch { } }
                throw;
            }
            return outs;
        }
    }
}
