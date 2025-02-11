﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
// jogeurte 11/19

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace WDAC_Wizard
{
    /// <summary>
    /// The local SiPolicy class for all policy manipulation
    /// </summary>
    public class WDAC_Policy
    {
        /// <summary>
        /// Enum to handle the type of policy being created/manipulated, e.g. Base, Supplemental, Edit or Merge workflows
        /// </summary>
        public enum PolicyType
        {
            None, 
            BasePolicy, 
            SupplementalPolicy, 
        }

        /// <summary>
        /// The base policy format. Can be either legacy (single policy) or multiple policy (19H1+)
        /// </summary>
        public enum Format
        {
            None,
            Legacy,
            MultiPolicy
        }

        /// <summary>
        /// Tracks the workflow the user has selected. New policy, editing policy or merging policies
        /// </summary>
        public enum Workflow
        {
            None,
            New,
            Edit,
            Merge
        }

        /// <summary>
        /// The template being leveraged in the new single or multiple policy creation workflow
        /// </summary>
        public enum NewPolicyTemplate
        {
            None, 
            AllowMicrosoft,
            WindowsWorks, 
            SignedReputable, 
        }

        // Policy Properties
        public PolicyType _PolicyType { get; set; }
        public NewPolicyTemplate _PolicyTemplate { get; set; }
        public Workflow PolicyWorkflow { get; set; }
        public Format _Format { get; set; }
        public List<RuleType> PolicyRuleOptions;

        public string PolicyName { get; set; }          // User entered friendly name for policy
        public string PolicyID { get; set; }
        public bool EnableHVCI { get; set; }            // Configure hypervisor code integrity (HVCI)?
        public bool EnableAudit { get; set; }           // Turn on audit mode? 
        public string VersionNumber { get; set; }       // Policy version. By default, 10.0.0.0.

        // Policy Settings
        public bool UseUserModeBlocks { get; set; }
        public bool UseKernelModeBlocks { get; set; }

        // Paths:
        public string SchemaPath { get; set; }          // Path to final xml file on disk
        public string TemplatePath { get; set; }        // ReadOnly Path to template policy - TODO: make const
        public string BaseToSupplementPath { get; set; } // Path to base policy to supplement, if applicable
        public string EditPolicyPath { get; set; }      // Path to the policy we are editing. Used for parsing.
        public string BinPath { get; set;  }

        public List<string> PoliciesToMerge { get; set; }


        // Datastructs for signing rules (and exceptions)
        public List<PolicyEKUs> EKUs { get; set; }
        public Dictionary<string, PolicyFileRules> FileRules { get; set; }
        public Dictionary<string, PolicySigners> Signers { get; set; }
        public List<PolicyUpdateSigners> UpdateSigners { get; set; }
        public List<PolicySupplementalSigners> SupplementalSigners { get; set; }
        public List<PolicyCISigners> CISigners { get; set; }
        public List<PolicySigningScenarios> SigningScenarios { get; set; }
        public List<PolicySettings> PolicySettings { get; set; }
        public Dictionary<string, Dictionary<string, string>> ConfigRules { get; set; }

        public List<PolicyCustomRules> CustomRules { get; set; }

        public SiPolicy siPolicy;

        public WDAC_Policy()
        {
            this.siPolicy = null; 
            this.PolicyRuleOptions = new List<RuleType>();

            this.EnableHVCI = false;
            this.EnableAudit = true;

            this.EKUs = new List<PolicyEKUs>();
            this.FileRules = new Dictionary<string, PolicyFileRules>();
            this.Signers = new Dictionary<string, PolicySigners>();//<PolicySigners>();
            this.SigningScenarios = new List<PolicySigningScenarios>();
            this.UpdateSigners = new List<PolicyUpdateSigners>();
            this.SupplementalSigners = new List<PolicySupplementalSigners>();
            this.CISigners = new List<PolicyCISigners>();
            this.PolicySettings = new List<PolicySettings>();
            this.CustomRules = new List<PolicyCustomRules>();
            this.PoliciesToMerge = new List<string>(); 

            this.VersionNumber = "10.0.0.0"; // Default policy version when calling the New-CIPolicy cmdlet
            this.PolicyID = Helper.GetFormattedDate();

            this.UseKernelModeBlocks = false;
            this.UseUserModeBlocks = false; 
        }

        /// <summary>
        /// Helper function to update the version number on a policy in edit. Will roll the version beginning with the LSB
        /// </summary>
        public string UpdateVersion()
        {
            int[] versionIdx = this.siPolicy.VersionEx.Split('.').Select(n => Convert.ToInt32(n)).ToArray(); 
            for (int i = versionIdx.Length-1; i > 0; i--)
            {
                if (versionIdx[i] >= UInt16.MaxValue)
                {
                    versionIdx[i] = 0;
                    versionIdx[i - 1]++;
                }
                else
                { 
                    versionIdx[i]++;
                    break;  
                }
            }

            // 65535.65535.65535.65535 will roll to 0.0.0.0
            if(versionIdx[0] > UInt16.MaxValue)
            {
                versionIdx[0] = 0; 
            }

            // Convert int[] --> this.VersionNumber string
            this.VersionNumber = ""; // reset string 
            foreach(var vIdx in versionIdx)
            {
                this.VersionNumber += String.Format("{0}.", vIdx.ToString());
            } 
            this.VersionNumber = this.VersionNumber.Substring(0, this.VersionNumber.Length - 1); //remove trailing period

            return this.VersionNumber; 
        }

        /// <summary>
        /// Determines whether the policy file contains a version number and position.
        /// </summary>
        /// <returns>Position of the _v_ in the filename. Returns 0 if the filename does not contain version number.</returns>
        public int EditPathContainsVersionInfo()
        {
            // Min length based on min version (0.0.0.0)
            int minFileNameLen = 7; 
            if (this.EditPolicyPath == null || this.EditPolicyPath.Length < minFileNameLen)
            {
                return 0;
            }

            // Find last instance of "_v" substring 
            string fileName = Path.GetFileNameWithoutExtension(this.EditPolicyPath);
            int index = fileName.LastIndexOf("_v"); 
            if (index < 0)
            {
                return 0; 
            }

            // Assert 3 dots to denote version
            var parts = fileName.Substring(index).Split('.');
            if(parts.Length < 4)
            {
                // Fewer than 3 version fields
                return 0;
            }

            // Return the index pos + length of dir
            return index + Path.GetDirectoryName(this.EditPolicyPath).Length + 1; 
        }

        /// <summary>
        /// Checks if a given rule option is already specified in the Policy
        /// </summary>
        /// <param name="targetRuleOption">Rule OptionType to query the Policy object for</param>
        /// <returns></returns>
        public bool HasRuleOption(OptionType targetRuleOption)
        {
            foreach(var ruleOption in this.PolicyRuleOptions)
            {
                if(ruleOption.Item == targetRuleOption)
                {
                    return true; 
                }
            }

            return false; 
        }

        /// <summary>
        /// Checks if a given rule option is already specified in the Policy
        /// </summary>
        /// <param name="targetRuleOption">Rule OptionType to query the Policy object for</param>
        /// <returns></returns>
        public bool HasRuleType(OptionType targetRuleOption)
        {
            foreach (var ruleOption in this.siPolicy.Rules)
            {
                if (ruleOption.Item == targetRuleOption)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public class COM
    {
        const string COMVALUENAME = "EnterpriseDefinedClsId";

        public enum ProviderType
        {
            None,
            PowerShell,
            WSH,
            IE,
            VBA,
            MSI,
            AllHostIds
        }

        public string Guid { get; set; }
        public ProviderType Provider { get; set; }

        public bool ValueItem { get; set; }
        public string ValueName { get; }

        public COM()
        {
            this.Provider = ProviderType.None;
            this.ValueName = COMVALUENAME;
        }

        /// <summary>
        /// Checks whether the COM Guid is valid. Returns true if "All Keys" or custom Guid is properly formed with or without {}
        /// </summary>
        /// <returns>True/False</returns>
        public bool IsValidRule()
        {
            // Possible solution 1: All Keys
            if(this.Guid.Equals(Properties.Resources.ComObjectAllKeys))
            {
                return true; 
            }

            // Possible solution 2: Valid GUID Format
            System.Guid _guid = System.Guid.NewGuid(); 
            return System.Guid.TryParse(this.Guid, out _guid); 
        }
    }

    public class FolderScan
    {
        // Rule Levels. The first in the list will be passed in as the -Level
        // The rest will be passed in as -Fallback
        public List<string> Levels { get; set; }
        
        // Struct to store the paths to omit while scanning
        public List<string> OmitPaths { get; set; }

        public FolderScan()
        {
            this.Levels = new List<string>();
            this.OmitPaths = new List<string>(); 
        }
    }

}
