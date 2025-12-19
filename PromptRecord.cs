using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper.Configuration.Attributes;


// POCO class for storing prompt records
public class PromptRecord
{
    [Name("prompt_text")]
    public string PromptText { get; set; } = string.Empty;
}
