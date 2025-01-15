using Chilano.Xbox360.Graphics;
using Chilano.Xbox360.IO;
using Chilano.Xbox360.Iso;
using Chilano.Xbox360.Xbe;
using Chilano.Xbox360.Xex;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using static System.Collections.Specialized.BitVector32;
using System.Xml;
using System.Text.RegularExpressions;

namespace Chilano.Iso2God;

internal class IsoDetails : BackgroundWorker
{
    private IsoDetailsArgs args;

    private FileStream f;

    private GDF iso;

    public IsoDetails()
    {
        base.WorkerReportsProgress = true;
        base.WorkerSupportsCancellation = false;
        base.DoWork += IsoDetails_DoWork;
    }

    private void IsoDetails_DoWork(object sender, DoWorkEventArgs e)
    {
        if (e.Argument == null)
        {
            throw new ArgumentNullException("A populated instance of IsoDetailsArgs must be passed.");
        }
        args = (IsoDetailsArgs)e.Argument;
        if (!openIso())
        {
            return;
        }
        IsoDetailsPlatform isoDetailsPlatform;

        if (iso.Exists("default.xex"))
        {
            isoDetailsPlatform = IsoDetailsPlatform.Xbox360;

            if(iso.Exists("default.xbe"))
            {
                // This is a dual platform game (Original Xbox and Xbox 360 on the same disc)
                // Warn the user we're going to create an xbox 360 GOD package, discarding the original xbox version
                MessageBox.Show("Dual platform game detected (both Xbox 360 and original Xbox). An Xbox 360 GOD package will be created and the original Xbox version will be discarded.", "Dual platform game", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            if (!iso.Exists("default.xbe"))
            {
                ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Could not locate default.xex or default.xbe."));
                return;
            }
            isoDetailsPlatform = IsoDetailsPlatform.Xbox;
        }
        switch (isoDetailsPlatform)
        {
            case IsoDetailsPlatform.Xbox:
                readXbe(e);
                break;
            case IsoDetailsPlatform.Xbox360:
                readXex(e);
                break;
        }
    }

    private bool openIso()
    {
        try
        {
            f = new FileStream(args.PathISO, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            iso = new GDF(f);
        }
        catch (IOException ex)
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Failed to open ISO image. Reason:\n\n" + ex.Message));
            return false;
        }
        catch (Exception ex2)
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Unhandled exception occured when opening ISO image. Reason:\n\n" + ex2.Message));
            return false;
        }
        return true;
    }

    public static string calcMD5(byte[] input)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] hashBytes = md5.ComputeHash(input);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hashBytes.Length; i++)
            {
                sb.Append(hashBytes[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }

    private void readXbe(DoWorkEventArgs e)
    {
        IsoDetailsResults isoDetailsResults = null;
        byte[] array = null;
        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Locating default.xbe..."));
        try
        {
            array = iso.GetFile("default.xbe");
        }
        catch (Exception ex)
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Unable to extract default.xbe. Reason:\n\n" + ex.Message));
            return;
        }
        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Found! Reading default.xbe..."));
        using (XbeInfo xbeInfo = new XbeInfo(array))
        {
            if (!xbeInfo.IsValid)
            {
                ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Default.xbe was not valid."));
                return;
            }
            isoDetailsResults = new IsoDetailsResults(xbeInfo.Certifcate.TitleName, xbeInfo.Certifcate.TitleID, (xbeInfo.Certifcate.DiskNumber != 0) ? xbeInfo.Certifcate.DiskNumber.ToString() : "1", calcMD5(array));
            isoDetailsResults.DiscCount = "1";
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Extracting thumbnail..."));

            thumbXpr(xbeInfo.Sections, "$$XSIMAGE", isoDetailsResults);

            if (isoDetailsResults.Thumbnail == null)
            {
                thumbXpr(xbeInfo.Sections, "$$XTIMAGE", isoDetailsResults);
            }
        }
        e.Result = isoDetailsResults;
    }

    private void thumbXpr(List<XbeSection> sections, string name, IsoDetailsResults isoDetailsResults)
    {
        foreach (XbeSection section in sections)
        {
            if (!(section.Name == name))
            {
                continue;
            }
            try
            {
                XPR xPR = new XPR(section.Data);
                //File.WriteAllBytes(name + ".xpr", section.Data);
                DDS dDS = xPR.ConvertToDDS(xPR.Width, xPR.Height);
                Bitmap bitmap = new Bitmap(xPR.Width, xPR.Height);
                switch (xPR.Format)
                {
                    case XPRFormat.ARGB:
                        bitmap = (Bitmap)dDS.GetImage(DDSType.ARGB);
                        break;
                    case XPRFormat.DXT1:
                        bitmap = (Bitmap)dDS.GetImage(DDSType.DXT1);
                        break;
                }
                Image image = new Bitmap(64, 64);
                Graphics graphics = Graphics.FromImage(image);
                graphics.DrawImage(bitmap, 0, 0, 64, 64);
                MemoryStream memoryStream = new MemoryStream();
                image.Save(memoryStream, ImageFormat.Png);
                isoDetailsResults.Thumbnail = (Image)image.Clone();
                isoDetailsResults.RawThumbnail = (byte[])memoryStream.ToArray().Clone();
                memoryStream.Dispose();
                bitmap.Dispose();
                graphics.Dispose();
            }
            catch (Exception ex)
            {
                ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Failed to convert thumbnail DDS to PNG.\n\n" + ex.Message));
            }
        }
    }

    private void readXex(DoWorkEventArgs e)
    {
        IsoDetailsResults isoDetailsResults = null;
        byte[] array = null;
        string text = null;
        string text2 = null;

        String xexResString = "";
        XmlDocument xexResources = new XmlDocument();

        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Locating default.xex..."));
        try
        {
            array = iso.GetFile("default.xex");
            text2 = args.PathTemp;
            text = text2 + "default.xex";
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Extracting default.xex..."));
            if (array == null || array.Length == 0)
            {
                ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Couldn't locate default.xex. Please check this ISO is valid."));
                return;
            }
            File.WriteAllBytes(text, array);
        }
        catch (Exception ex)
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "A problem occured when reading the contents of the ISO image.\n\nPlease ensure this is a valid Xbox 360 ISO by running it through ABGX360.\n\n" + ex.Message));
            return;
        }
        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Found! Reading default.xex..."));
        using (XexInfo xexInfo = new XexInfo(array))
        {
            if (!xexInfo.IsValid)
            {
                ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Default.xex is not valid."));
                return;
            }
            if (xexInfo.Header.ContainsKey(XexInfoFields.ExecutionInfo))
            {
                XexExecutionInfo xexExecutionInfo = (XexExecutionInfo)xexInfo.Header[XexInfoFields.ExecutionInfo];
                isoDetailsResults = new IsoDetailsResults("", DataConversion.BytesToHexString(xexExecutionInfo.TitleID), DataConversion.BytesToHexString(xexExecutionInfo.MediaID), xexExecutionInfo.Platform.ToString(), xexExecutionInfo.ExecutableType.ToString(), xexExecutionInfo.DiscNumber.ToString(), xexExecutionInfo.DiscCount.ToString(), null);
            }
        }
        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Extracting resources..."));
        Process process = new Process();
        process.EnableRaisingEvents = false;
        process.StartInfo.FileName = args.PathXexTool;
        if (!File.Exists(process.StartInfo.FileName))
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Couldn't locate XexTool. Expected location was:\n" + process.StartInfo.FileName + "\n\nTry disabling User Access Control if it's enabled."));
            return;
        }
        process.StartInfo.WorkingDirectory = text2;
        process.StartInfo.Arguments = "-xa default.xex";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;

        // Standard redirection buffers are too small and xextool will hang.
        process.OutputDataReceived += (procSender, procE) =>
        {
            if (procE.Data != null)
            {
                xexResString += procE.Data;
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.WaitForExit();
            process.Close();
        }
        catch (Win32Exception)
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Could not launch XexTool!"));
            return;
        }

        // Load the output from xextool into an xml document
        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Parsing xextool output..."));

        try
        {
            // Fix any wonky negative character codes from xextool
            foreach (Match m in Regex.Matches(xexResString, "&#(-\\d+);"))
            {
                string s = m.Value.Substring(2, m.Value.Length - 3);

                byte intVal = (byte)sbyte.Parse(s);

                xexResString = xexResString.Replace(m.Value, "&#" + intVal.ToString() + ";");
            }

            // Load an XML document, stripping everything before the first open bracket
            // from the xextool output
            xexResources.LoadXml(xexResString.Substring(xexResString.IndexOf('<')));
        }
        catch
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Failed to parse xextool output!"));
            return;
        }

        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Extracting name..."));
        try
        {
            isoDetailsResults.Name = xexResources.DocumentElement.SelectSingleNode("/XexInfo/GameName").InnerText;
        }
        catch (Exception)
        {
            isoDetailsResults.Name = "Unable to read name.";
        }
        
        ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Progress, "Extracting thumbnail..."));

        try
        {
            byte[] resource = Convert.FromBase64String(xexResources.DocumentElement.SelectSingleNode("/XexInfo/GameIcon").InnerText);
            MemoryStream stream = new MemoryStream(resource);
            Image image = Image.FromStream(stream);
            isoDetailsResults.Thumbnail = (Image)image.Clone();
            isoDetailsResults.RawThumbnail = (byte[])resource.Clone();
            image.Dispose();
        }
        catch (Exception)
        {
            ReportProgress(0, new IsoDetailsResults(IsoDetailsResultsType.Error, "Couldn't find thumbnail in xextool output."));
        }

        e.Result = isoDetailsResults;
    }
}
