/* A separate process receives parent-validated scalars, never source pages or
 * SQL. Its only writable filesystem subtree is an empty destination staging
 * directory. All statements and schema definitions are compiled into the worker. */
static const char *inserts[] = {
    "INSERT INTO format VALUES(?,?,?,?,?,?)",
    "INSERT INTO strings VALUES(?,?)",
    "INSERT INTO artifacts VALUES(?,?,?)",
    "INSERT INTO occurrences VALUES(?,?,?,?,?,?,?,?,?)",
    "INSERT INTO fields VALUES(?,?,?,?,?,?,?,?,?)",
    "INSERT INTO snapshots VALUES(?,?,?)"
};

static int rebuild_authorize(int action,const char *first,const char *second)
{
    (void)second;
    if (action==21 || action==22) return 0;
    if (action==19 && first && configuring &&
        (!strcmp(first,"cache_size") || !strcmp(first,"mmap_size") || !strcmp(first,"journal_mode") ||
         !strcmp(first,"synchronous") || !strcmp(first,"temp_store") || !strcmp(first,"foreign_keys") ||
         !strcmp(first,"max_page_count") || !strcmp(first,"locking_mode"))) return 0;
    if (rebuild_ddl && (action==1 || action==2 || action==18 || action==20 || action==23)) return 0;
    if (rebuild_ddl && action==27 && first)
        for (int i=0;i<10;i++) if (!strcmp(first,index_names[i])) return 0;
    if (first && (action==18 || action==20))
        for (int i=0;i<6;i++) if (!strcmp(first,table_names[i])) return 0;
    rebuild_denied_action=action;
    return 1;
}

static void rebuild_database(const char *uri)
{
    int64_t capacity, vm;
    read_exact(&capacity,8); read_exact(&vm,8);
    valid(capacity>=65536 && capacity<=256LL*1024*1024 && vm>0 && vm<=200000000,"Rebuild.Configuration");
    instruction_limit=vm;
    sqlite3 *db=open_configured(uri);
    configuring=1;
    execute(db,"PRAGMA journal_mode=DELETE");
    execute(db,"PRAGMA synchronous=FULL");
    execute(db,"PRAGMA temp_store=MEMORY");
    execute(db,"PRAGMA foreign_keys=ON");
    execute(db,"PRAGMA locking_mode=EXCLUSIVE");
    char page_limit[80];
    int n=snprintf(page_limit,sizeof(page_limit),"PRAGMA max_page_count=%lld",(long long)(capacity/4096));
    valid(n>0 && (size_t)n<sizeof(page_limit),"Rebuild.Configuration");
    valid(scalar(db,page_limit)==capacity/4096,"Rebuild.PageLimit");
    configuring=0;
    rebuild_ddl=1;
    for(int i=0;i<12;i++) execute(db,definitions[i]);
    rebuild_ddl=0;
    execute(db,"BEGIN IMMEDIATE");
    sqlite3_stmt *statements[6];
    for(int i=0;i<6;i++) statements[i]=prepare(db,inserts[i]);
    unsigned char bytes[65536];
    unsigned char *value=NULL;
    uint32_t value_length=0,value_offset=0;
    int type=0,table=-1,previous=-1,column=0;
    int64_t input_bytes=16,total=0;
    int ended=0;
    while(!ended) {
        uint32_t length;
        read_exact(&length,4);
        valid(length>=1 && length<=sizeof(bytes),"Limit.FrameBytes");
        input_bytes+=4+length;
        valid(input_bytes<=512LL*1024*1024,"Limit.WireBytes");
        read_exact(bytes,length);
        if(bytes[0]==1) {
            valid(length==3 && table==-1 && bytes[1]<=5 && bytes[1]>=previous &&
                bytes[2]==column_counts[bytes[1]],"Rebuild.Row");
            table=previous=bytes[1]; column=0;
            valid(++counts[table]<=2000000 && ++total<=4000000,"Limit.Rows");
        } else if(bytes[0]==2) {
            valid(length==6 && table>=0 && !value && column<column_counts[table],"Rebuild.Cell");
            type=bytes[1]; memcpy(&value_length,bytes+2,4); value_offset=0;
            valid(type>=1 && type<=5 && (type!=5 || value_length==0) &&
                ((type!=1 && type!=2) || value_length==8),"Rebuild.StorageClass");
            valid(value_length <= (type==4 ? 8U*1024*1024 : 65536U),"Limit.CellBytes");
            if(value_length) {
                value=malloc(value_length);
                valid(value!=NULL,"Limit.RebuildBuffer");
            } else {
                int result=type==5 ? sql_bind_null(statements[table],++column) :
                    type==3 ? sql_bind_text(statements[table],++column,"",0,NULL) :
                    sql_bind_blob(statements[table],++column,"",0,NULL);
                valid(result==0,"Rebuild.Bind");
            }
        } else if(bytes[0]==3) {
            valid(value && length>1 && length-1<=value_length-value_offset,"Rebuild.Value");
            memcpy(value+value_offset,bytes+1,length-1); value_offset+=length-1;
            if(value_offset==value_length) {
                int result;
                column++;
                if(type==1) { int64_t v; memcpy(&v,value,8); result=sql_bind_int64(statements[table],column,v); }
                else if(type==2) { double v; memcpy(&v,value,8); valid(isfinite(v),"Rebuild.NonFinite");
                    result=sql_bind_double(statements[table],column,v); }
                else if(type==3) {
                    (void)text_cost(value,(int)value_length);
                    result=sql_bind_text(statements[table],column,(const char *)value,(int)value_length,(void (*)(void *))-1);
                } else result=sql_bind_blob(statements[table],column,value,(int)value_length,(void (*)(void *))-1);
                free(value); value=NULL;
                valid(result==0,"Rebuild.Bind");
            }
        } else if(bytes[0]==4) {
            valid(length==1 && table>=0 && !value && column==column_counts[table],"Rebuild.RowEnd");
            int result=tracked_step(statements[table]);
            if(result==13) admission_error("Limit.DatabaseBytes");
            valid(result==101 && sql_reset(statements[table])==0,"Rebuild.Insert");
            table=-1;
        } else if(bytes[0]==5) {
            valid(length==65 && table==-1 && counts[0]==1,"Rebuild.End");
            int64_t expected[6]; memcpy(expected,bytes+1,48);
            for(int i=0;i<6;i++) valid(expected[i]==counts[i],"Rebuild.Population");
            unsigned char extra;
            valid(read(0,&extra,1)==0,"Rebuild.Trailing");
            ended=1;
        } else admission_error("Rebuild.Frame");
    }
    for(int i=0;i<6;i++) valid(sql_finalize(statements[i])==0,"Rebuild.Finalize");
    execute(db,"COMMIT");
    valid(sql_close(db)==0,"Rebuild.Close");
    frame(6,&instructions,8);
}
