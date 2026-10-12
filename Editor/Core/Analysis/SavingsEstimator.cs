using System;
using System.Collections.Generic;
using UnityEngine;
namespace ApexForge.ProjectOptimizer
{
    /// <summary>Derives byte estimates from measurements an analyzer already collected; never invents a percentage without a measurable pool.</summary>
    public static class SavingsEstimator
    {
        public static void Enrich(AssetEntry entry)
        {
            if(entry==null||entry.Issues.Count==0)return;
            for(int i=0;i<entry.Issues.Count;i++){
                var issue=entry.Issues[i]; if(issue==null||issue.EstimatedSavings>0||issue.Basis==SavingsBasis.Unknown)continue;
                long memory=entry.RuntimeSize>0?entry.RuntimeSize:0, disk=entry.FileSize>0?entry.FileSize:0, value=0;
                switch(issue.RuleId){
                    case "MDL001": value=GetLong(entry,"meshCpuBytes"); break;
                    case "MDL008": value=GetLong(entry,"tangentBytes"); break;
                    case "MDL010": value=GetLong(entry,"indexBytes32")/2; break;
                    case "MDL014": value=GetLong(entry,"secondaryUvBytes"); break;
                    case "TEX004": value=GetLong(entry,"readWriteCpuBytes"); break;
                    case "TEX005": value=GetLong(entry,"mipSavingsBytes"); break;
                    case "TEX017": value=GetLong(entry,"globalMipLimitSavingsBytes"); break;
                    case "ATL001": value=GetLong(entry,"generatedReadableBytes"); break;
                    case "ATL006": value=GetLong(entry,"mipSavingsBytes"); break;
                    case "FNT005": value=EstimateSingleChannel(memory,entry); break;
                }
                if(value<=0)continue;
                if(issue.Basis==SavingsBasis.Memory) issue.EstimatedSavings=Math.Min(value,Math.Max(0,memory));
                else if(issue.Basis==SavingsBasis.Disk) issue.EstimatedSavings=Math.Min(value,Math.Max(0,disk));
            }
        }
        static long GetLong(AssetEntry e,string key){if(!e.Fields.TryGetValue(key,out var v)||v==null)return 0;try{return Math.Max(0,Convert.ToInt64(v,System.Globalization.CultureInfo.InvariantCulture));}catch{return 0;}}
        static long EstimateSingleChannel(long memory,AssetEntry e){ if(memory<=0)return 0; string f=e.Field("format") as string; if(string.IsNullOrEmpty(f))return 0; string l=f.ToLowerInvariant(); if(l.Contains("rgba32")||l.Contains("argb32")||l.Contains("bgra32"))return memory/4*3; if(l.Contains("rgb24"))return memory/3*2; return 0; }
    }
}
