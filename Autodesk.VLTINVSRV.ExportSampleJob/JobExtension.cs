/*=====================================================================
  
  This file is an educational sample working with Autodesk Vault API.

  Copyright (C) Autodesk Inc.  All rights reserved.

THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
PARTICULAR PURPOSE.
=====================================================================*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using Autodesk.Connectivity.Extensibility.Framework;
using ACET = Autodesk.Connectivity.Explorer.ExtensibilityTools;
using VDF = Autodesk.DataManagement.Client.Framework;
using Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections;
using Autodesk.DataManagement.Client.Framework.Vault.Currency.Properties;
using Autodesk.DataManagement.Client.Framework.Vault.Settings;
using Autodesk.DataManagement.Client.Framework.Currency;

using Autodesk.Connectivity.JobProcessor.Extensibility;
using ACW = Autodesk.Connectivity.WebServices;
using Inventor;
using Microsoft.SqlServer.Server;
using System.Linq.Expressions;

[assembly: ApiVersion("15.0")]
[assembly: ExtensionId("5e7c8112-b00a-4c97-b41c-899aef60fee6")]


namespace Autodesk.VltInvSrv.ExportSampleJob
{
    public class JobExtension : IJobHandler
    {
        private static string JOB_TYPE = "Autodesk.VltInvSrv.BerchtenbreiterExport";
        private static Settings mSettings = Settings.Load();
        private static string mLogDir = JobExtension.mSettings.LogFileLocation;
        private static string mLogFile = JOB_TYPE + ".log";
        private TextWriterTraceListener mTrace = new TextWriterTraceListener(System.IO.Path.Combine(
            mLogDir, mLogFile), "mJobTrace");

        #region IJobHandler Implementation
        public bool CanProcess(string jobType)
        {
            return jobType == JOB_TYPE;
        }

        public JobOutcome Execute(IJobProcessorServices context, IJob job)
        {
            try
            {
                //Temp Ornder bereinigen
                FileInfo mLogFileInfo = new FileInfo(System.IO.Path.Combine(
                    mLogDir, mLogFile));
                if (mLogFileInfo.Exists) mLogFileInfo.Delete();

                string tempDxfFolder = @"C:\Temp\BerchtenbreiterExport";

                try
                {
                    if (Directory.Exists(tempDxfFolder))
                    {
                        foreach (string actFile in Directory.GetFiles(tempDxfFolder))
                        {
                            System.IO.File.SetAttributes(actFile, FileAttributes.Normal);
                            System.IO.File.Delete(actFile);
                        }

                        Directory.Delete(tempDxfFolder, true);
                    }
                }
                catch
                {
                    mTrace.WriteLine("BerchtenbreiterExport konnte nicht bereinigt werden da der Ordner von einem anderen Prozess verwendet wird.");
                }

                try
                {
                    string[] files = Directory.GetFiles(@"C:\temp\", "FilterConfig.xml", SearchOption.AllDirectories);
                    foreach (string file in files)
                    {
                        Directory.Delete(file.Replace("FilterConfig.xml", ""), true);
                    }
                }
                catch
                {
                    mTrace.WriteLine("FilterConfig.xml konnte nicht bereinigt werden da die Datei von einem anderen Prozess verwendet wird.");
                }
                mTrace.WriteLine("Starting Job...");

                //start step export
                mCreateExport(context, job);

                mTrace.IndentLevel = 0;
                mTrace.WriteLine("... successfully ending Job.");
                mTrace.Flush();
                mTrace.Close();

                return JobOutcome.Success;
            }
            catch (Exception ex)
            {
                context.Log(ex, "Autodesk.STEP.ExportSampleJob failed: " + ex.ToString() + " ");

                mTrace.IndentLevel = 0;
                mTrace.WriteLine("... ending Job with failures.");
                mTrace.Flush();
                mTrace.Close();

                return JobOutcome.Failure;
            }
        }

        public void OnJobProcessorShutdown(IJobProcessorServices context)
        {
            //throw new NotImplementedException();
        }

        public void OnJobProcessorSleep(IJobProcessorServices context)
        {
            //throw new NotImplementedException();
        }

        public void OnJobProcessorStartup(IJobProcessorServices context)
        {
            //throw new NotImplementedException();
        }

        public void OnJobProcessorWake(IJobProcessorServices context)
        {
            //throw new NotImplementedException();
        }
        #endregion IJobHandler Implementation

        #region Job Execution
        private void mCreateExport(IJobProcessorServices context, IJob job)
        {
            List<string> mExpFrmts = new List<string>();
            List<string> mUploadFiles = new List<string>();

            // read target export formats from settings file
            Settings settings = Settings.Load();

            #region validate execution rules

            mTrace.IndentLevel += 1;
            mTrace.WriteLine("Translator Job started...");

            //pick up this job's context
            Connection connection = context.Connection;
            Autodesk.Connectivity.WebServicesTools.WebServiceManager mWsMgr = connection.WebServiceManager;
            long mEntId = Convert.ToInt64(job.Params["EntityId"]);
            string mEntClsId = job.Params["EntityClassId"];

            // only run the job for files
            if (mEntClsId != "FILE")
                return;

            // geändert auf nur ipt
            List<string> mFileExtensions = new List<string> { ".ipt" };
            ACW.File mFile = mWsMgr.DocumentService.GetFileById(mEntId);
            if (!mFileExtensions.Any(n => mFile.Name.Contains(n)))
            {
                return;
            }

            // apply execution filters, e.g., exclude files of classification "substitute" etc.
            List<string> mFileClassific = new List<string> { "ConfigurationFactory", "DesignSubstitute" }; //add "DesignDocumentation" for 3D Exporters only
            if (mFileClassific.Any(n => mFile.FileClass.ToString().Contains(n)))
            {
                return;
            }

            // you may add addtional execution filters

            if (settings.ExportFomats == null)
                throw new Exception("Settings expect to list at least one export format!");
            if (settings.ExportFomats.Contains(","))
            {
                mExpFrmts = settings.ExportFomats.Split(',').ToList();
            }
            else
            {
                mExpFrmts.Add(settings.ExportFomats);
            }

            //remove SM formats, if source isn't sheet metal category name == "Muster_Konstruktion oder Konstruktion"
            if (mFile.Cat.CatName != "Muster_Konstruktion" & mFile.Cat.CatName != "Konstruktion")
            {
                if (mExpFrmts.Contains("SMDXF")) mExpFrmts.Remove("SMDXF");
                if (mExpFrmts.Contains("SMSAT")) mExpFrmts.Remove("SMSAT");
            }

            mTrace.WriteLine("Job execution rules validated.");

            #endregion validate execution rules

            #region VaultInventorServer IPJ activation
            //establish InventorServer environment including translator addins; differentiate her in case full Inventor.exe is used
            Inventor.InventorServer mInv = context.InventorObject as InventorServer;
            ApplicationAddIns mInvSrvAddIns = mInv.ApplicationAddIns;

            //override InventorServer default project settings by your Vault specific ones
            Inventor.DesignProjectManager projectManager;
            Inventor.DesignProject mSaveProject, mProject;
            String mIpjPath = "";
            String mWfPath = "";
            String mIpjLocalPath = "";
            ACW.File mProjFile;
            VDF.Vault.Currency.Entities.FileIteration mIpjFileIter = null;

            //download and activate the Inventor Project file in VaultInventorServer
            mTrace.IndentLevel += 1;
            mTrace.WriteLine("Job tries activating Inventor project file as enforced in Vault behavior configurations.");
            try
            {
                //Download enforced ipj file
                if (mWsMgr.DocumentService.GetEnforceWorkingFolder() && mWsMgr.DocumentService.GetEnforceInventorProjectFile())
                {
                    mIpjPath = mWsMgr.DocumentService.GetInventorProjectFileLocation();
                    mWfPath = mWsMgr.DocumentService.GetRequiredWorkingFolderLocation();
                }
                else
                {
                    throw new Exception("Job requires both settings enabled: 'Enforce Workingfolder' and 'Enforce Inventor Project'.");
                }

                String[] mIpjFullFileName = mIpjPath.Split(new string[] { "/" }, StringSplitOptions.None);
                String mIpjFileName = mIpjFullFileName.LastOrDefault();

                //get the projects file object for download
                ACW.PropDef[] filePropDefs = mWsMgr.PropertyService.GetPropertyDefinitionsByEntityClassId("FILE");
                ACW.PropDef mNamePropDef = filePropDefs.Single(n => n.SysName == "ClientFileName");
                ACW.SrchCond mSrchCond = new ACW.SrchCond()
                {
                    PropDefId = mNamePropDef.Id,
                    PropTyp = ACW.PropertySearchType.SingleProperty,
                    SrchOper = 3, // is equal
                    SrchRule = ACW.SearchRuleType.Must,
                    SrchTxt = mIpjFileName
                };
                string bookmark = string.Empty;
                ACW.SrchStatus status = null;
                List<ACW.File> totalResults = new List<ACW.File>();
                while (status == null || totalResults.Count < status.TotalHits)
                {
                    ACW.File[] results = mWsMgr.DocumentService.FindFilesBySearchConditions(new ACW.SrchCond[] { mSrchCond },
                        null, null, false, true, ref bookmark, out status);
                    if (results != null)
                        totalResults.AddRange(results);
                    else
                        break;
                }
                if (totalResults.Count == 1)
                {
                    mProjFile = totalResults[0];
                }
                else
                {
                    throw new Exception("Job execution stopped due to ambigous project file definitions; single project file per Vault expected");
                }

                //define download settings for the project file
                VDF.Vault.Settings.AcquireFilesSettings mDownloadSettings = new VDF.Vault.Settings.AcquireFilesSettings(connection);
                mDownloadSettings.LocalPath = new VDF.Currency.FolderPathAbsolute(mWfPath);
                mIpjFileIter = new VDF.Vault.Currency.Entities.FileIteration(connection, mProjFile);
                mDownloadSettings.AddFileToAcquire(mIpjFileIter, VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);

                //download project file and get local path
                VDF.Vault.Results.AcquireFilesResults mDownLoadResult;
                VDF.Vault.Results.FileAcquisitionResult fileAcquisitionResult;
                mDownLoadResult = connection.FileManager.AcquireFiles(mDownloadSettings);
                fileAcquisitionResult = mDownLoadResult.FileResults.FirstOrDefault();
                mIpjLocalPath = fileAcquisitionResult.LocalPath.FullPath;

                //activate this Vault's ipj temporarily    
                projectManager = mInv.DesignProjectManager;
                mSaveProject = projectManager.ActiveDesignProject;
                mProject = projectManager.DesignProjects.AddExisting(mIpjLocalPath);
                mProject.Activate();

                //[Optionally:] get Inventor Design Data settings and download all related files ---------

                mTrace.WriteLine("Job successfully activated Inventor IPJ.");
            }
            catch (Exception ex)
            {
                throw new Exception("Job was not able to activate Inventor project file. - Note: The ipj must not be checked out by another user.", ex.InnerException);
            }
            #endregion VaultInventorServer IPJ activation

            #region download source file(s)
            mTrace.IndentLevel += 1;
            mTrace.WriteLine("Job downloads source file(s) for translation.");
            //download the source file iteration, enforcing overwrite if local files exist
            VDF.Vault.Settings.AcquireFilesSettings mDownloadSettings2 = new VDF.Vault.Settings.AcquireFilesSettings(connection);
            VDF.Vault.Currency.Entities.FileIteration mFileIteration = new VDF.Vault.Currency.Entities.FileIteration(connection, mFile);
            mDownloadSettings2.AddFileToAcquire(mFileIteration, VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);
            mDownloadSettings2.OrganizeFilesRelativeToCommonVaultRoot = true;
            mDownloadSettings2.OptionsRelationshipGathering.FileRelationshipSettings.IncludeChildren = true;
            mDownloadSettings2.OptionsRelationshipGathering.FileRelationshipSettings.IncludeLibraryContents = true;
            mDownloadSettings2.OptionsRelationshipGathering.FileRelationshipSettings.ReleaseBiased = true;
            mDownloadSettings2.LocalPath = new VDF.Currency.FolderPathAbsolute(@"c:\Temp\BerchtenbreiterExport");
            VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions mResOpt = new VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions();
            mResOpt.OverwriteOption = VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.ForceOverwriteAll;
            mResOpt.SyncWithRemoteSiteSetting = VDF.Vault.Settings.AcquireFilesSettings.SyncWithRemoteSite.Always;

            //execute download
            VDF.Vault.Results.AcquireFilesResults mDownLoadResult2 = connection.FileManager.AcquireFiles(mDownloadSettings2);
            //pickup result details
            VDF.Vault.Results.FileAcquisitionResult fileAcquisitionResult2 = mDownLoadResult2.FileResults.Where(n => n.File.EntityName == mFileIteration.EntityName).FirstOrDefault();

            if (fileAcquisitionResult2 == null)
            {
                mSaveProject.Activate();
                throw new Exception("Job stopped execution as the source file to translate did not download");
            }
            string mDocPath = fileAcquisitionResult2.LocalPath.FullPath;
            string mExt = System.IO.Path.GetExtension(mDocPath); //mDocPath.Split('.').Last();
            mTrace.WriteLine("Job successfully downloaded source file(s) for translation.");
            #endregion download source file(s)

            #region VaultInventorServer CAD Export

            mTrace.WriteLine("Job opens source file.");
            Document mDoc = null;
            mDoc = mInv.Documents.Open(mDocPath);

            foreach (string item in mExpFrmts)
            {

                switch (item)
                {
                    case ("STP"):
                        //activate STEP Translator environment,
                        try
                        {
                            TranslatorAddIn mStepTrans = mInvSrvAddIns.ItemById["{90AF7F40-0C01-11D5-8E83-0010B541CD80}"] as TranslatorAddIn;
                            if (mStepTrans == null)
                            {
                                //switch temporarily used project file back to original one
                                mSaveProject.Activate();
                                throw new Exception("Job stopped execution, because indicated translator addin is not available.");
                            }
                            TranslationContext mTransContext = mInv.TransientObjects.CreateTranslationContext();
                            NameValueMap mTransOptions = mInv.TransientObjects.CreateNameValueMap();
                            if (mStepTrans.HasSaveCopyAsOptions[mDoc, mTransContext, mTransOptions] == true)
                            {
                                //open, and translate the source file
                                mTrace.IndentLevel += 1;
                                mTrace.WriteLine("Job opens source file.");
                                mTransOptions.Value["ApplicationProtocolType"] = 3; //AP 2014, Automotive Design
                                mTransOptions.Value["Description"] = "Sample-Job Step Translator using VaultInventorServer";
                                mTransContext.Type = IOMechanismEnum.kFileBrowseIOMechanism;
                                //delete local file if exists, as the export wouldn't overwrite
                                if (System.IO.File.Exists(mDocPath.Replace(mExt, ".stp")))
                                {
                                    System.IO.File.SetAttributes(mDocPath.Replace(mExt, ".stp"), System.IO.FileAttributes.Normal);
                                    System.IO.File.Delete(mDocPath.Replace(mExt, ".stp"));
                                };
                                DataMedium mData = mInv.TransientObjects.CreateDataMedium();
                                mData.FileName = mDocPath.Replace(mExt, ".stp");
                                mStepTrans.SaveCopyAs(mDoc, mTransContext, mTransOptions, mData);
                                //collect all export files for later upload
                                mUploadFiles.Add(mDocPath.Replace(mExt, ".stp"));
                                mTrace.WriteLine("STEP Translator created file: " + mUploadFiles.LastOrDefault());
                                mTrace.IndentLevel -= 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            mTrace.WriteLine("STEP Export Failed: " + ex.Message);
                        }
                        break;

                    case "JT":
                        //activate JT Translator environment,
                        try
                        {
                            TranslatorAddIn mJtTrans = mInvSrvAddIns.ItemById["{16625A0E-F58C-4488-A969-E7EC4F99CACD}"] as TranslatorAddIn;
                            if (mJtTrans == null)
                            {
                                //switch temporarily used project file back to original one
                                mTrace.WriteLine("JT Translator not found.");
                                break;
                            }
                            TranslationContext mTransContext = mInv.TransientObjects.CreateTranslationContext();
                            NameValueMap mTransOptions = mInv.TransientObjects.CreateNameValueMap();
                            if (mJtTrans.HasSaveCopyAsOptions[mDoc, mTransContext, mTransOptions] == true)
                            {
                                //open, and translate the source file
                                mTrace.IndentLevel += 1;

                                mTransOptions.Value["Version"] = 102; //default
                                mTransContext.Type = IOMechanismEnum.kFileBrowseIOMechanism;
                                //delete local file if exists, as the export wouldn't overwrite
                                if (System.IO.File.Exists(mDocPath.Replace(mExt, ".jt")))
                                {
                                    System.IO.File.SetAttributes(mDocPath.Replace(mExt, ".jt"), System.IO.FileAttributes.Normal);
                                    System.IO.File.Delete(mDocPath.Replace(mExt, ".jt"));
                                };
                                DataMedium mData = mInv.TransientObjects.CreateDataMedium();
                                mData.FileName = mDocPath.Replace(mExt, ".jt");
                                mJtTrans.SaveCopyAs(mDoc, mTransContext, mTransOptions, mData);
                                //collect all export files for later upload
                                mUploadFiles.Add(mDocPath.Replace(mExt, ".jt"));
                                mTrace.WriteLine("JT Translator created file: " + mUploadFiles.LastOrDefault());
                                mTrace.IndentLevel -= 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            mTrace.WriteLine("JT Export Failed: " + ex.Message);
                        }
                        break;

                    case "SMDXF":
                        try
                        {
                            TranslatorAddIn mDXFTrans = mInvSrvAddIns.ItemById["{C24E3AC4-122E-11D5-8E91-0010B541CD80}"] as TranslatorAddIn;
                            mDXFTrans.Activate();
                            if (mDXFTrans == null)
                            {
                                mTrace.WriteLine("DXF Translator not found.");
                                break;
                            }
                            PartDocument mPartDoc = (PartDocument)mDoc;

                            //Überprüfen ob es ein Blechteil ist

                            mTrace.IndentLevel += 1;

                            if (mPartDoc.SubType != "{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}")
                            {
                                mTrace.WriteLine("Abwicklung kann nur für Blechteile erstellt werden.");
                                mTrace.IndentLevel -= 1;
                                break;
                            }
                            SheetMetalComponentDefinition oCompDef = (SheetMetalComponentDefinition)mPartDoc.ComponentDefinition;

                            //Überprüfen ob Abwicklung vorhanden ist
                            if (oCompDef.HasFlatPattern == false)
                            {
                                mTrace.WriteLine("Keine Abwickung gefunden!");
                                mTrace.IndentLevel -= 1;
                                break;
                            }

                            //Upload Pfad und Dateinamen generieren
                            string mDocName = mFile.Name.Replace(mExt, "");
                            string posNo = "-1-";
                            var thickness = oCompDef.Thickness.ModelValue*100;
                            string blechdicke = thickness.ToString("000");
                            string material = mPartDoc.ComponentDefinition.Material.Name;
                            string uPath = "C:\\Temp\\BerchtenbreiterExport\\" + mDocName + posNo + blechdicke +"-"+ material + mExt;

                            if (System.IO.File.Exists(uPath.Replace(mExt, ".dxf")))
                            {
                                System.IO.FileInfo fileInfo = new FileInfo(uPath.Replace(mExt, ".dxf"));
                                fileInfo.IsReadOnly = false;
                                fileInfo.Delete();
                            }

                            DataIO mDataIO = mPartDoc.ComponentDefinition.DataIO;
                            String mOut = "FLAT PATTERN DXF?AcadVersion=2018"
                                + "&MergeProfilesIntoPolyline=false"
                                + "&RebaseGeometry=true"
                                + "&SimplifySplines=true"
                                + "&SplineTolerance=0,010000"
                                + "&OuterProfileLayer=0"
                                + "&InteriorProfilesLayer=0"
                                + "&FeatureProfilesLayer=IV_FEATURE_PROFILES"
                                + "&TangentLayer=IV_TANGENT"
                                + "&ToolCenterLayer=IV_TOOL_CENTER"
                                + "&ToolCenterUpLayer=IV_TOOL_CENTER_UP"
                                + "&ToolCenterDownLayer=IV_TOOL_CENTER_DOWN"
                                + "&ArcCentersLayer=IV_ARC_CENTERS"
                                + "&InvisibleLayers=IV_FEATURE_PROFILES;IV_TANGENT;IV_BEND;IV_TOOL_CENTER;IV_TOOL_CENTER_UP;IV_TOOL_CENTER_DOWN;IV_ARC_CENTERS;IV_BEND_DOWN";
                            mDataIO.WriteDataToFile(mOut, uPath.Replace(mExt, ".dxf"));
                            //collect all export files for later upload
                            mUploadFiles.Add(uPath.Replace(mExt, ".dxf"));
                            mTrace.WriteLine("SheetMetal DXF Translator created file: " + mUploadFiles.LastOrDefault());
                            mTrace.IndentLevel -= 1;
                        }
                        catch (Exception ex)
                        {
                            mTrace.WriteLine("SMDXF Export Failed: " + ex.Message);
                        }
                        break;

                    default:
                        break;
                }
            }
            mDoc.Close(true);
            mTrace.WriteLine("Source file closed");

            //switch temporarily used project file back to original one
            mSaveProject.Activate();

            mTrace.WriteLine("Job exported file(s); continues uploading.");
            mTrace.IndentLevel -= 1;

            #endregion VaultInventorServer CAD Export

            #region Vault File Management

            foreach (string file in mUploadFiles)
            {
                ACW.File mExpFile = null;
                System.IO.FileInfo mExportFileInfo = new System.IO.FileInfo(file);
                if (mExportFileInfo.Exists)
                {
                    //copy file to output location
                    if (settings.OutPutPath != "")
                    {
                        System.IO.FileInfo fileInfo = new FileInfo(settings.OutPutPath + "\\" + mExportFileInfo.Name);
                        if (fileInfo.Exists)
                        {
                            fileInfo.IsReadOnly = false;
                            fileInfo.Delete();
                        }
                        System.IO.File.Copy(mExportFileInfo.FullName, settings.OutPutPath + "\\" + mExportFileInfo.Name, true);
                    }

                    //add resulting export file to Vault if it doesn't exist, otherwise update the existing one
                    //$/AUFTRÄGE NEU/Dokumentation/LaserDxf/ - FolderID 1591724
                    ACW.Folder mFolder = mWsMgr.DocumentService.FindFoldersByIds(new long[] { 1591724 }).FirstOrDefault();
                    
                    string vaultFilePath = System.IO.Path.Combine(mFolder.FullName, mExportFileInfo.Name).Replace("\\", "/");
                    ACW.File wsFile = mWsMgr.DocumentService.FindLatestFilesByPaths(new string[] { vaultFilePath }).First();
                    VDF.Currency.FilePathAbsolute vdfPath = new VDF.Currency.FilePathAbsolute(mExportFileInfo.FullName);
                    VDF.Vault.Currency.Entities.FileIteration vdfFile = null;
                    VDF.Vault.Currency.Entities.FileIteration addedFile = null;
                    VDF.Vault.Currency.Entities.FileIteration mUploadedFile = null;
                    if (wsFile == null || wsFile.Id < 0)
                    {
                        // add new file to Vault
                        mTrace.WriteLine("Job adds " + mExportFileInfo.Name + " as new file.");

                        if (mFolder == null || mFolder.Id == -1)
                            throw new Exception("Vault folder " + mFolder.FullName + " not found");

                        var folderEntity = new Autodesk.DataManagement.Client.Framework.Vault.Currency.Entities.Folder(connection, mFolder);
                        try
                        {
                            addedFile = connection.FileManager.AddFile(folderEntity, "Created by Job Processor", null, null, ACW.FileClassification.DesignRepresentation, false, vdfPath);
                            mExpFile = addedFile;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception("Job could not add export file " + vdfPath + "Exception: ", ex);
                        }

                    }
                    else
                    {
                        // checkin new file version
                        mTrace.WriteLine("Job uploads " + mExportFileInfo.Name + " as new file version.");

                        VDF.Vault.Settings.AcquireFilesSettings aqSettings = new VDF.Vault.Settings.AcquireFilesSettings(connection)
                        {
                            DefaultAcquisitionOption = VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Checkout
                        };
                        vdfFile = new VDF.Vault.Currency.Entities.FileIteration(connection, wsFile);
                        aqSettings.AddEntityToAcquire(vdfFile);
                        var results = connection.FileManager.AcquireFiles(aqSettings);
                        try
                        {
                            mUploadedFile = connection.FileManager.CheckinFile(results.FileResults.First().File, "Created by Job Processor", false, null, null, false, null, ACW.FileClassification.DesignRepresentation, false, vdfPath);
                            mExpFile = mUploadedFile;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception("Job could not update existing export file " + vdfFile + "Exception: ", ex);
                        }
                    }
                }
                else
                {
                    throw new Exception("Job could not find the export result file: " + mDocPath.Replace(mExt, ".stp"));
                }

                mTrace.IndentLevel += 1;

                //update the new file's revision
                try
                {
                    mTrace.WriteLine("Job tries synchronizing " + mExpFile.Name + "'s revision in Vault.");
                    mWsMgr.DocumentServiceExtensions.UpdateFileRevisionNumbers(new long[] { mExpFile.Id }, new string[] { mFile.FileRev.Label }, "Rev Index synchronized by Job Processor");
                }
                catch (Exception)
                {
                    //the job will not stop execution in this sample, if revision labels don't synchronize
                }

                //synchronize source file properties to export file properties for UDPs assigned to both
                try
                {
                    mTrace.WriteLine(mExpFile.Name + ": Job tries synchronizing properties in Vault.");
                    //get the design rep category's user properties
                    ACET.IExplorerUtil mExplUtil = Autodesk.Connectivity.Explorer.ExtensibilityTools.ExplorerLoader.LoadExplorerUtil(
                                connection.Server, connection.Vault, connection.UserID, connection.Ticket);
                    Dictionary<ACW.PropDef, object> mPropDictonary = new Dictionary<ACW.PropDef, object>();

                    //get property definitions filtered to UDPs
                    VDF.Vault.Currency.Properties.PropertyDefinitionDictionary mPropDefDic = connection.PropertyManager.GetPropertyDefinitions(
                        VDF.Vault.Currency.Entities.EntityClassIds.Files, null, VDF.Vault.Currency.Properties.PropertyDefinitionFilter.IncludeUserDefined);

                    VDF.Vault.Currency.Properties.PropertyDefinition mPropDef = new PropertyDefinition();
                    ACW.PropInst[] mSourcePropInsts = mWsMgr.PropertyService.GetProperties("FILE", new long[] { mFile.Id }, new long[] { mPropDef.Id });

                    //get property definitions assigned to Design Representation category
                    ACW.CatCfg catCfg1 = mWsMgr.CategoryService.GetCategoryConfigurationById(mExpFile.Cat.CatId, new string[] { "UserDefinedProperty" });
                    List<long> mFilePropDefs = new List<long>();
                    foreach (ACW.Bhv bhv in catCfg1.BhvCfgArray.First().BhvArray)
                    {
                        mFilePropDefs.Add(bhv.Id);
                    }

                    //get properties assigned to source file and add definition/value pair to dictionary
                    mSourcePropInsts = mWsMgr.PropertyService.GetProperties("FILE", new long[] { mFile.Id }, mFilePropDefs.ToArray());
                    ACW.PropDef[] propDefs = connection.WebServiceManager.PropertyService.GetPropertyDefinitionsByEntityClassId("FILE");
                    foreach (ACW.PropInst item in mSourcePropInsts)
                    {
                        mPropDef = connection.PropertyManager.GetPropertyDefinitionById(item.PropDefId);
                        ACW.PropDef propDef = propDefs.SingleOrDefault(n => n.Id == item.PropDefId);
                        mPropDictonary.Add(propDef, item.Val);
                    }

                    //update export file using the property dictionary; note this the IExplorerUtil method bumps file iteration and requires no check out
                    mExplUtil.UpdateFileProperties(mExpFile, mPropDictonary);

                }
                catch (Exception ex)
                {
                    mTrace.WriteLine("Job failed copying properties from source file " + mFile.Name + " to export file: " + mExpFile.Name + " . Exception details: " + ex);
                    //you may uncomment the action below if the job should abort executing due to failures copying property values
                    //throw new Exception("Job failed copying properties from source file " + mFile.Name + " to export file: " + mExpFile.Name + " . Exception details: " + ex.ToString() + " ");
                }

                //align lifecycle states of export to source file's state name
                try
                {
                    mTrace.WriteLine(mExpFile.Name + ": Job tries synchronizing lifecycle state in Vault.");
                    Dictionary<string, long> mTargetStateNames = new Dictionary<string,long>();
                    long mReleasedStateId = -1;
                    ACW.LfCycDef mTargetLfcDef = (mWsMgr.LifeCycleService.GetLifeCycleDefinitionsByIds(new long[] { mExpFile.FileLfCyc.LfCycDefId })).FirstOrDefault();
                    foreach (var item in mTargetLfcDef.StateArray)
                    {
                        if (item.ReleasedState == true && mReleasedStateId == -1)
                        {
                            mReleasedStateId = item.Id;
                        }
                        mTargetStateNames.Add(item.DispName, item.Id);
                    }
                    mTargetStateNames.TryGetValue(mFile.FileLfCyc.LfCycStateName, out long mTargetLfcStateId);
                    //handle the case that state names differ, especially for the ReleasedState
                    if (mTargetLfcStateId == 0)
                    {
                        if (mFile.FileLfCyc.Consume == true && mReleasedStateId != -1) // it is a "ReleasedState"
                        {
                            mTargetLfcStateId = mReleasedStateId;
                        }
                    }
                    if (mTargetLfcStateId != 0)
                    {
                        mWsMgr.DocumentServiceExtensions.UpdateFileLifeCycleStates(new long[] { mExpFile.MasterId }, new long[] { mTargetLfcStateId }, "Lifecycle state synchronized by Job Processor");
                    }
                    else
                    {
                        mTrace.WriteLine(mExpFile.Name + "WARNING: Job could not synchronize lifecycle state in Vault.");
                    }
                }
                catch (Exception)
                {
                    mTrace.WriteLine(mExpFile.Name + "Unhandled Exception: Job could not synchronize lifecycle state in Vault.");
                }

                //attach export file to source file leveraging design representation attachment type
                try
                {
                    mTrace.WriteLine(mExpFile.Name + ": Job tries to attach to its source in Vault.");
                    ACW.FileAssocParam mAssocParam = new ACW.FileAssocParam();
                    mAssocParam.CldFileId = mExpFile.Id;
                    mAssocParam.ExpectedVaultPath = mWsMgr.DocumentService.FindFoldersByIds(new long[] { mFile.FolderId }).First().FullName;
                    mAssocParam.RefId = null;
                    mAssocParam.Source = null;
                    mAssocParam.Typ = ACW.AssociationType.Attachment;
                    mWsMgr.DocumentService.AddDesignRepresentationFileAttachment(mFile.Id, mAssocParam);
                }
                catch (Exception)
                {
                    mTrace.WriteLine(mExpFile.Name + "Unhandled Exception: Job could not attach to its source in Vault.");
                }

                mTrace.IndentLevel -= 1;

            }

            #endregion Vault File Management

            mTrace.IndentLevel = 1;
            mTrace.WriteLine("Job finished all steps.");
            mTrace.Flush();
            mTrace.Close();
        }
        #endregion Job Execution
    }
}
