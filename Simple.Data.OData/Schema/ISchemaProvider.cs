﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Simple.Data.OData.Schema
{
    public interface ISchemaProvider
    {
        IEnumerable<Table> GetTables();
        IEnumerable<Column> GetColumns(Table table);
        IEnumerable<Association> GetAssociations(Table table);
        Key GetPrimaryKey(Table table);
        IEnumerable<Function> GetFunctions();
    }
}
