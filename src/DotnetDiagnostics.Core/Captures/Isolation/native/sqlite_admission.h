/* Internal wire v1 and scalar/schema admission. Snapshot representation and
 * provenance validation are deliberately NOT asserted by this milestone. */
#include <math.h>
#include <sys/stat.h>

static const char *table_names[] = {"format","strings","artifacts","occurrences","fields","snapshots"};
static const char *definitions[] = {
    "CREATE TABLE format(package INTEGER NOT NULL,schema_version INTEGER NOT NULL,record_version INTEGER NOT NULL,index_version INTEGER NOT NULL,writer_version INTEGER NOT NULL,reader_version INTEGER NOT NULL)",
    "CREATE TABLE strings(id INTEGER PRIMARY KEY,value TEXT NOT NULL UNIQUE)",
    "CREATE TABLE artifacts(id TEXT PRIMARY KEY,kind TEXT NOT NULL,name TEXT NOT NULL)",
    "CREATE TABLE occurrences(id INTEGER PRIMARY KEY,artifact_id TEXT NOT NULL REFERENCES artifacts(id),timestamp_ticks INTEGER,thread_id INTEGER,category_id INTEGER REFERENCES strings(id),name_id INTEGER REFERENCES strings(id),numeric_value REAL,duration_ns INTEGER,unit_id INTEGER REFERENCES strings(id))",
    "CREATE TABLE fields(record_id INTEGER NOT NULL REFERENCES occurrences(id),ordinal INTEGER NOT NULL,name_id INTEGER NOT NULL REFERENCES strings(id),kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 4),string_id INTEGER REFERENCES strings(id),int_value INTEGER,double_value REAL,bool_value INTEGER,unit_id INTEGER REFERENCES strings(id),PRIMARY KEY(record_id,ordinal))",
    "CREATE TABLE snapshots(artifact_id TEXT PRIMARY KEY REFERENCES artifacts(id),version INTEGER NOT NULL,json BLOB NOT NULL)",
    "CREATE INDEX ix_occurrence_artifact ON occurrences(artifact_id,id)",
    "CREATE INDEX ix_occurrence_time ON occurrences(artifact_id,timestamp_ticks,id)",
    "CREATE INDEX ix_occurrence_thread ON occurrences(artifact_id,thread_id,id)",
    "CREATE INDEX ix_occurrence_category ON occurrences(artifact_id,category_id,id)",
    "CREATE INDEX ix_occurrence_name ON occurrences(artifact_id,name_id,id)",
    "CREATE INDEX ix_fields_name ON fields(name_id,record_id)"
};
static const char *index_names[] = {"ix_occurrence_artifact","ix_occurrence_time","ix_occurrence_thread",
    "ix_occurrence_category","ix_occurrence_name","ix_fields_name",
    "sqlite_autoindex_strings_1","sqlite_autoindex_artifacts_1","sqlite_autoindex_fields_1","sqlite_autoindex_snapshots_1"};
static const char *index_columns[][3] = {
    {"artifact_id","id",NULL},{"artifact_id","timestamp_ticks","id"},{"artifact_id","thread_id","id"},
    {"artifact_id","category_id","id"},{"artifact_id","name_id","id"},{"name_id","record_id",NULL},
    {"value",NULL,NULL},{"id",NULL,NULL},{"record_id","ordinal",NULL},{"artifact_id",NULL,NULL}
};
static const int column_counts[] = {6,2,3,9,9,3};
static int schema_allowed;
static int integrity_allowed;
static int64_t counts[6], total_rows, logical_bytes, wire_bytes;
static uint32_t max_rows, max_total, max_record, max_fields, max_snapshot, max_artifacts;
static int64_t max_logical, expected_persisted;
static int expected_format[6], descriptor_count;
static struct { char id[33], kind[1025], name[1025]; int kind_length, name_length; } descriptors[64];

static void write_exact(const void *data, size_t count)
{
    const unsigned char *p = data;
    while (count) {
        ssize_t n = write(1, p, count);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) _exit(74);
        p += n; count -= (size_t)n;
    }
}

static void admission_error(const char *reason)
{
    uint32_t size = (uint32_t)strlen(reason) + 1;
    unsigned char tag = 127;
    write_exact(&size, 4); write_exact(&tag, 1); write_exact(reason, size - 1);
    _exit(79);
}

static void valid(int condition, const char *reason)
{
    if (!condition) admission_error(reason);
}

static void frame(unsigned char tag, const void *data, uint32_t size)
{
    uint32_t length = size + 1;
    valid(length <= 65536, "Limit.FrameBytes");
    wire_bytes += 4 + length;
    valid(wire_bytes <= 512LL * 1024 * 1024, "Limit.WireBytes");
    write_exact(&length, 4); write_exact(&tag, 1);
    if (size) write_exact(data, size);
}

static void read_exact(void *data, size_t count)
{
    unsigned char *p = data;
    while (count) {
        ssize_t n = read(0, p, count);
        if (n < 0 && errno == EINTR) continue;
        valid(n > 0, "Request.Truncated");
        p += n; count -= (size_t)n;
    }
}

/* Strict Unicode scalar decoding, including overlong sequences, surrogates,
 * embedded NULs (valid text data), and the UTF-16 cost used by CaptureWriter. */
static int64_t text_cost(const unsigned char *p, int length)
{
    int64_t units = 0;
    for (int i = 0; i < length;) {
        unsigned int c = p[i++];
        int extra = 0;
        unsigned int minimum = 0;
        if (c < 128) { units++; continue; }
        if (c >= 0xc2 && c <= 0xdf) { extra=1; minimum=0x80; c &= 31; }
        else if (c >= 0xe0 && c <= 0xef) { extra=2; minimum=0x800; c &= 15; }
        else if (c >= 0xf0 && c <= 0xf4) { extra=3; minimum=0x10000; c &= 7; }
        else admission_error("Data.Utf8");
        valid(i + extra <= length, "Data.Utf8");
        while (extra--) { unsigned int b=p[i++]; valid((b & 0xc0)==0x80,"Data.Utf8"); c=(c<<6)|(b&63); }
        valid(c>=minimum && c<=0x10ffff && !(c>=0xd800 && c<=0xdfff),"Data.Utf8");
        units += c>=0x10000 ? 2 : 1;
    }
    return 24 + units * 2 + length;
}

static int is_id(const unsigned char *p, int length)
{
    if (length != 32) return 0;
    for (int i=0;i<32;i++) if (!((p[i]>='0'&&p[i]<='9')||(p[i]>='a'&&p[i]<='f'))) return 0;
    return 1;
}

static int64_t integer(sqlite3_stmt *s,int column,int nullable)
{
    int type=sql_type(s,column);
    valid(type==1 || (nullable && type==5),"Data.StorageClass");
    return type==5 ? 0 : sql_int64(s,column);
}

static int64_t text(sqlite3_stmt *s,int column,int maximum,int nullable,int id)
{
    int type=sql_type(s,column);
    if (type==5 && nullable) return 0;
    valid(type==3,"Data.StorageClass");
    int n=sql_bytes(s,column);
    valid(n<=maximum,"Limit.TextBytes");
    const unsigned char *p=sql_text(s,column);
    valid(p!=NULL,"Data.TextRead");
    if (id) valid(is_id(p,n),"Data.Id");
    return text_cost(p,n);
}

static void real_value(sqlite3_stmt *s,int column)
{
    int type=sql_type(s,column);
    valid(type==5 || type==2,"Data.StorageClass");
    if (type==2) valid(isfinite(sql_double(s,column)),"Data.NonFinite");
}

static sqlite3_stmt *prepare(sqlite3 *db,const char *query)
{
    sqlite3_stmt *s=NULL;
    int result=sql_prepare(db,query,-1,&s,NULL);
    if (result==9) admission_error("Limit.VmInstructions");
    if (result==7) admission_error("Limit.SqliteHeap");
    valid(result==0,"Sqlite.Prepare");
    return s;
}

static int step(sqlite3_stmt *s)
{
    int result=tracked_step(s);
    if (result==9) admission_error("Limit.VmInstructions");
    if (result==7) admission_error("Limit.SqliteHeap");
    valid(result==100 || result==101,"Sqlite.Step");
    return result==100;
}

static void row_count(int table)
{
    valid(++counts[table]<=max_rows,"Limit.RowsPerTable");
    valid(++total_rows<=max_total,"Limit.RowsPerCapture");
}

static int admission_authorize(int action,const char *first,const char *second)
{
    (void)second;
    if (action==21) return 0;
    if (action==20 && first) {
        if (!strcmp(first,"sqlite_master") || !strcmp(first,"sqlite_schema")) return 0;
        if (schema_allowed) for(int i=0;i<6;i++) if (!strcmp(first,table_names[i])) return 0;
    }
    if (action==19 && first) {
        if (configuring && (!strcmp(first,"cache_size") || !strcmp(first,"mmap_size"))) return 0;
        if (integrity_allowed && (!strcmp(first,"quick_check") || !strcmp(first,"foreign_key_check")
            || !strcmp(first,"index_xinfo") || !strcmp(first,"index_list"))) return 0;
    }
    return 1;
}

/* Token comparison, not whitespace removal: quoted identifiers may differ only
 * in the known SQLite producer quoting; string literals are never normalized. */
static int token(const char **p,char *out)
{
    while (**p==' ' || **p=='\t' || **p=='\r' || **p=='\n') (*p)++;
    if (!**p) return 0;
    int n=0;
    char c=*(*p)++;
    int quoted=c=='"' || c=='`' || c=='[';
    if (c=='"' || c=='`' || c=='[') {
        char end=c=='[' ? ']' : c;
        while (**p && **p!=end) { valid(n<127,"Schema.Token"); out[n++]=*(*p)++; }
        valid(**p==end,"Schema.Token"); (*p)++;
        valid(n>0,"Schema.Token");
    } else if ((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_') {
        out[n++]=c;
        while ((**p>='A'&&**p<='Z')||(**p>='a'&&**p<='z')||(**p>='0'&&**p<='9')||**p=='_') {
            valid(n<127,"Schema.Token"); out[n++]=*(*p)++;
        }
    } else {
        valid(c!='\'' && c!=';' && c!='-' && c!='/',"Schema.Token");
        out[n++]=c;
    }
    for (int i=0;i<n;i++) if (out[i]>='A'&&out[i]<='Z') out[i]+=32;
    out[n]=0;
    if (quoted) {
        const char *columns[]={"package","schema_version","record_version","index_version","writer_version","reader_version",
            "id","value","kind","name","artifact_id","timestamp_ticks","thread_id","category_id","name_id",
            "numeric_value","duration_ns","unit_id","record_id","ordinal","string_id","int_value","double_value",
            "bool_value","version","json"};
        int identifier=0;
        for(int i=0;i<6;i++) if (!strcmp(out,table_names[i])) identifier=1;
        for(int i=0;i<6;i++) if (!strcmp(out,index_names[i])) identifier=1;
        for(size_t i=0;i<sizeof(columns)/sizeof(columns[0]);i++) if (!strcmp(out,columns[i])) identifier=1;
        valid(identifier,"Schema.QuotedToken");
    }
    return 1;
}

static int definition_matches(const char *actual,const char *expected)
{
    char a[128],b[128];
    while (1) {
        int x=token(&actual,a),y=token(&expected,b);
        if (x!=y) return 0;
        if (!x) return 1;
        if (strcmp(a,b)) return 0;
    }
}

static void schema(sqlite3 *db)
{
    sqlite3_stmt *s=prepare(db,"SELECT type,name,tbl_name,rootpage,sql FROM sqlite_schema;");
    unsigned int seen=0;
    int entries=0,sql_size=0;
    while(step(s)) {
        valid(++entries<=32,"Limit.SchemaEntries");
        for(int i=0;i<3;i++) text(s,i,128,0,0);
        for(int i=0;i<3;i++) valid(strlen((const char *)sql_text(s,i))==(size_t)sql_bytes(s,i),"Schema.Text");
        valid(integer(s,3,0)>0,"Schema.RootPage");
        const char *type=(const char *)sql_text(s,0),*name=(const char *)sql_text(s,1),*owner=(const char *)sql_text(s,2);
        int index=-1;
        for(int i=0;i<6;i++) if (!strcmp(name,table_names[i])) index=i;
        for(int i=0;i<10;i++) if (!strcmp(name,index_names[i])) index=6+i;
        valid(index>=0 && !(seen&(1U<<index)),"Schema.Object");
        seen|=1U<<index;
        valid(!strcmp(type,index<6 ? "table" : "index"),"Schema.ObjectType");
        const char *table=index<6 ? table_names[index] : index<11 ? "occurrences" :
            index==11 ? "fields" : index==12 ? "strings" : index==13 ? "artifacts" : index==14 ? "fields" : "snapshots";
        valid(!strcmp(owner,table),"Schema.Owner");
        if(index<12) {
            text(s,4,32768,0,0);
            valid(strlen((const char *)sql_text(s,4))==(size_t)sql_bytes(s,4),"Schema.Text");
            sql_size+=sql_bytes(s,4);
            valid(sql_size<=32768,"Limit.SchemaSqlBytes");
            valid(definition_matches((const char *)sql_text(s,4),definitions[index]),"Schema.Definition");
        } else valid(sql_type(s,4)==5,"Schema.AutoIndex");
    }
    sql_finalize(s);
    valid(seen==65535,"Schema.MissingObject");
    schema_allowed=1;
    integrity_allowed=1;
    unsigned int indexes=0;
    for(int t=0;t<6;t++) {
        char query[80];
        snprintf(query,sizeof(query),"PRAGMA index_list('%s');",table_names[t]);
        s=prepare(db,query);
        int n=0;
        while(step(s)) {
            valid(++n<=6,"Schema.IndexCount");
            text(s,1,128,0,0); text(s,3,8,0,0);
            int found=-1;
            for(int i=0;i<10;i++) if (!strcmp((const char *)sql_text(s,1),index_names[i])) found=i;
            valid(found>=0 && !(indexes&(1U<<found)),"Schema.IndexList");
            indexes|=1U<<found;
            valid(integer(s,2,0)==(found>=6) && integer(s,4,0)==0,"Schema.IndexUniqueness");
            valid(!strcmp((const char *)sql_text(s,3),found<6?"c":found==6?"u":"pk"),"Schema.IndexOrigin");
        }
        sql_finalize(s);
    }
    valid(indexes==1023,"Schema.IndexList");
    for(int i=0;i<10;i++) {
        char query[180];
        snprintf(query,sizeof(query),"PRAGMA index_xinfo('%s');",index_names[i]);
        s=prepare(db,query);
        int key=0,rows=0;
        while(step(s)) {
            valid(++rows<=4,"Schema.IndexShape");
            integer(s,0,0); integer(s,1,0); integer(s,3,0); integer(s,5,0);
            text(s,4,16,0,0);
            valid(!strcmp((const char *)sql_text(s,4),"BINARY") && sql_int64(s,3)==0,"Schema.IndexCollation");
            if(sql_int64(s,5)==0) {
                valid(sql_type(s,2)==5 && sql_int64(s,1)==-1,"Schema.IndexAuxiliary");
                continue;
            }
            valid(key<3 && index_columns[i][key]!=NULL,"Schema.IndexOrder");
            text(s,2,64,0,0);
            valid(!strcmp((const char *)sql_text(s,2),index_columns[i][key]),"Schema.IndexOrder");
            key++;
        }
        valid(key>0 && (key==3 || index_columns[i][key]==NULL),"Schema.IndexOrder");
        sql_finalize(s);
    }
    s=prepare(db,"PRAGMA quick_check(1);");
    valid(step(s),"Integrity.QuickCheck");
    text(s,0,1024,0,0);
    valid(!strcmp((const char *)sql_text(s,0),"ok") && !step(s),"Integrity.QuickCheck");
    sql_finalize(s);
    s=prepare(db,"PRAGMA foreign_key_check;");
    valid(!step(s),"Data.ForeignKey");
    sql_finalize(s);
    integrity_allowed=0;
}

static void emit_row(sqlite3_stmt *s,int table)
{
    unsigned char begin[2]={(unsigned char)table,(unsigned char)column_counts[table]};
    frame(1,begin,2);
    for(int i=0;i<column_counts[table];i++) {
        int type=sql_type(s,i);
        uint32_t length=type==5 ? 0 : (type==1 || type==2) ? 8 : (uint32_t)sql_bytes(s,i);
        unsigned char header[5]; header[0]=(unsigned char)type; memcpy(header+1,&length,4);
        frame(2,header,5);
        const void *data=NULL;
        int64_t integer_value=0; double double_value=0;
        if(type==1) { integer_value=sql_int64(s,i); data=&integer_value; }
        else if(type==2) { double_value=sql_double(s,i); data=&double_value; }
        else if(type==3) data=sql_text(s,i);
        else if(type==4) data=sql_blob(s,i);
        const unsigned char *p=data;
        while(length) { uint32_t n=length>65535 ? 65535 : length; frame(3,p,n); p+=n; length-=n; }
    }
    frame(4,NULL,0);
}

static void no_rows(sqlite3 *db,const char *query,const char *reason)
{
    sqlite3_stmt *s=prepare(db,query);
    valid(!step(s),reason);
    sql_finalize(s);
}

static void data(sqlite3 *db)
{
    sqlite3_stmt *s=prepare(db,"SELECT package,schema_version,record_version,index_version,writer_version,reader_version FROM format;");
    valid(step(s),"Data.FormatMissing"); row_count(0);
    for(int i=0;i<6;i++) valid(integer(s,i,0)==expected_format[i],"Format.Mismatch");
    emit_row(s,0); valid(!step(s),"Data.FormatDuplicate"); sql_finalize(s);

    s=prepare(db,"SELECT id,value FROM strings ORDER BY id;");
    int64_t previous=0;
    while(step(s)) {
        row_count(1); int64_t id=integer(s,0,0);
        valid(id>previous,"Data.StringId"); previous=id;
        text(s,1,(int)max_record,0,0); emit_row(s,1);
    }
    sql_finalize(s);
    no_rows(db,"SELECT s.id FROM strings s WHERE NOT EXISTS(SELECT 1 FROM occurrences o WHERE o.category_id=s.id OR o.name_id=s.id OR o.unit_id=s.id) AND NOT EXISTS(SELECT 1 FROM fields f WHERE f.name_id=s.id OR f.string_id=s.id OR f.unit_id=s.id) LIMIT 1;","Data.UnusedString");

    s=prepare(db,"SELECT id,kind,name FROM artifacts ORDER BY id;");
    unsigned int matched[64]={0};
    while(step(s)) {
        row_count(2); valid(counts[2]<=max_artifacts,"Limit.Artifacts");
        text(s,0,32,0,1); text(s,1,1024,0,0); text(s,2,1024,0,0);
        int found=0;
        for(int i=0;i<descriptor_count;i++) if (!strcmp((const char *)sql_text(s,0),descriptors[i].id)) {
            valid(!matched[i]++ && sql_bytes(s,1)==descriptors[i].kind_length &&
                sql_bytes(s,2)==descriptors[i].name_length &&
                !memcmp(sql_text(s,1),descriptors[i].kind,(size_t)sql_bytes(s,1)) &&
                !memcmp(sql_text(s,2),descriptors[i].name,(size_t)sql_bytes(s,2)),"Data.ArtifactDescriptor");
            found=1;
        }
        valid(found,"Data.ArtifactDescriptor"); emit_row(s,2);
    }
    sql_finalize(s);
    valid(counts[2]==descriptor_count,"Data.ArtifactDescriptor");

    s=prepare(db,"SELECT id,artifact_id,timestamp_ticks,thread_id,category_id,name_id,numeric_value,duration_ns,unit_id FROM occurrences ORDER BY id;");
    sqlite3_stmt *dimensions=prepare(db,"SELECT c.value,n.value,u.value FROM occurrences o LEFT JOIN strings c ON c.id=o.category_id LEFT JOIN strings n ON n.id=o.name_id LEFT JOIN strings u ON u.id=o.unit_id WHERE o.id=?1;");
    sqlite3_stmt *fields=prepare(db,"SELECT f.ordinal,f.kind,n.value,s.value,f.int_value,f.double_value,f.bool_value,u.value FROM fields f JOIN strings n ON n.id=f.name_id LEFT JOIN strings s ON s.id=f.string_id LEFT JOIN strings u ON u.id=f.unit_id WHERE f.record_id=?1 ORDER BY f.ordinal;");
    previous=0;
    while(step(s)) {
        row_count(3); int64_t id=integer(s,0,0);
        valid(id>previous,"Data.OccurrenceId"); previous=id;
        text(s,1,32,0,1);
        int64_t timestamp=integer(s,2,1);
        valid(timestamp>=0 && timestamp<=3155378975999999999LL,"Data.Timestamp");
        integer(s,3,1); integer(s,4,1); integer(s,5,1); real_value(s,6);
        valid(integer(s,7,1)>=0,"Data.Duration"); integer(s,8,1);
        valid(sql_bind_int64(dimensions,1,id)==0,"Sqlite.Bind"); valid(step(dimensions),"Data.Dimension");
        int64_t size=128;
        for(int i=0;i<3;i++) size+=text(dimensions,i,(int)max_record,1,0);
        valid(!step(dimensions) && sql_reset(dimensions)==0,"Data.Dimension");
        valid(sql_bind_int64(fields,1,id)==0,"Sqlite.Bind");
        int ordinal=0;
        while(step(fields)) {
            valid(integer(fields,0,0)==ordinal++,"Data.FieldOrdinal");
            valid(ordinal<=(int)max_fields,"Limit.Fields");
            int64_t kind=integer(fields,1,0);
            valid(kind>=0 && kind<=4,"Data.FieldKind");
            size+=64+text(fields,2,(int)max_record,0,0)+text(fields,3,(int)max_record,1,0)+text(fields,7,(int)max_record,1,0);
            integer(fields,4,1); real_value(fields,5); int64_t boolean=integer(fields,6,1);
            valid((kind==1)==(sql_type(fields,3)!=5) && (kind==2)==(sql_type(fields,4)!=5)
                && (kind==3)==(sql_type(fields,5)!=5) && (kind==4)==(sql_type(fields,6)!=5)
                && (sql_type(fields,6)==5 || boolean==0 || boolean==1),"Data.ScalarSlots");
            valid(size<=max_record,"Limit.RecordBytes");
        }
        valid(sql_reset(fields)==0,"Sqlite.Reset");
        valid(size<=max_record,"Limit.RecordBytes");
        logical_bytes+=size; valid(logical_bytes<=max_logical,"Limit.LogicalBytes");
        emit_row(s,3);
    }
    sql_finalize(s); sql_finalize(fields); sql_finalize(dimensions);
    valid(counts[3]==expected_persisted,"Data.PersistedPopulation");
    s=prepare(db,"SELECT record_id,ordinal,name_id,kind,string_id,int_value,double_value,bool_value,unit_id FROM fields ORDER BY record_id,ordinal;");
    while(step(s)) {
        row_count(4);
        for(int i=0;i<9;i++) if(i!=6) integer(s,i,i>=4);
        real_value(s,6); emit_row(s,4);
    }
    sql_finalize(s);
    s=prepare(db,"SELECT artifact_id,version,json FROM snapshots ORDER BY artifact_id;");
    while(step(s)) {
        row_count(5); text(s,0,32,0,1);
        int64_t version=integer(s,1,0);
        valid(version>0 && version<=2147483647,"Data.SnapshotVersion");
        valid(sql_type(s,2)==4,"Data.StorageClass");
        int bytes=sql_bytes(s,2);
        valid(bytes>0 && (uint32_t)bytes<=max_snapshot,"Limit.SnapshotBytes");
        const void *blob=sql_blob(s,2);
        valid(blob!=NULL,"Limit.SqliteHeap");
        text_cost(blob,bytes);
        logical_bytes+=bytes; valid(logical_bytes<=max_logical,"Limit.LogicalBytes");
        emit_row(s,5);
    }
    sql_finalize(s);
}

static unsigned char request[128*1024];
static uint32_t request_size, request_at;
static uint32_t request_u32(void)
{
    valid(request_at+4<=request_size,"Request.Truncated");
    uint32_t value; memcpy(&value,request+request_at,4); request_at+=4; return value;
}
static int64_t request_i64(void)
{
    valid(request_at+8<=request_size,"Request.Truncated");
    int64_t value; memcpy(&value,request+request_at,8); request_at+=8; return value;
}
static int request_text(char *out,uint32_t maximum)
{
    uint32_t n=request_u32();
    valid(n<=maximum && n<=request_size-request_at,"Request.Text");
    text_cost(request+request_at,(int)n);
    memcpy(out,request+request_at,n); out[n]=0; request_at+=n;
    return (int)n;
}
static uint32_t big32(const unsigned char *p)
{
    return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3];
}
static void header_check(const char *path)
{
    const char *suffixes[]={"-wal","-shm","-journal"};
    for(int i=0;i<3;i++) {
        char sidecar[4120];
        valid(snprintf(sidecar,sizeof(sidecar),"%s%s",path,suffixes[i])<(int)sizeof(sidecar),"Header.Path");
        int fd=open(sidecar,O_RDONLY|O_NOFOLLOW|O_CLOEXEC);
        valid(fd<0 && errno==ENOENT,"Header.ExternalJournal");
    }
    int fd=open(path,O_RDONLY|O_NOFOLLOW|O_CLOEXEC);
    valid(fd>=0,"Header.Open");
    struct stat info;
    valid(fstat(fd,&info)==0 && S_ISREG(info.st_mode),"Header.File");
    valid(info.st_size<=256LL*1024*1024,"Limit.DatabaseBytes");
    unsigned char h[100];
    valid(pread(fd,h,sizeof(h),0)==sizeof(h),"Header.Truncated");
    close(fd);
    uint32_t page=((uint32_t)h[16]<<8)|h[17];
    if(page==1) page=65536;
    valid(!memcmp(h,"SQLite format 3\0",16) && page>=512 && (page&(page-1))==0 &&
        (int64_t)big32(h+28)*page==info.st_size && big32(h+28)>0 &&
        (h[18]==1 || h[18]==2) && (h[19]==1 || h[19]==2) && h[20]==0 &&
        h[21]==64 && h[22]==32 && h[23]==32 && big32(h+56)==1 &&
        big32(h+24)==big32(h+92),"Header.GeometryOrEncoding");
    for(int i=72;i<92;i++) valid(h[i]==0,"Header.Reserved");
}
static void admit_database(const char *uri,const char *path)
{
    read_exact(&request_size,4);
    valid(request_size<=sizeof(request),"Limit.RequestBytes");
    read_exact(request,request_size);
    valid(request_u32()==1,"Request.Version");
    for(int i=0;i<6;i++) expected_format[i]=(int)request_u32();
    valid((expected_format[0]==1 || expected_format[0]==2) && expected_format[1]==1 &&
        expected_format[2]==1 && expected_format[3]==1 && expected_format[4]==expected_format[0] &&
        expected_format[5]==expected_format[0],"Format.Unsupported");
    expected_persisted=request_i64(); valid(expected_persisted>=0,"Request.Population");
    max_rows=request_u32(); max_total=request_u32(); max_record=request_u32(); max_fields=request_u32();
    max_snapshot=request_u32(); max_artifacts=request_u32(); max_logical=request_i64(); instruction_limit=request_i64();
    valid(max_rows>0 && max_rows<=2000000 && max_total>0 && max_total<=4000000 &&
        max_record>=128 && max_record<=65536 && max_fields<=64 && max_snapshot>0 && max_snapshot<=8*1024*1024 &&
        max_artifacts>0 && max_artifacts<=64 && max_logical>=128 && max_logical<=128*1024*1024 &&
        instruction_limit>=1000 && instruction_limit<=200000000,"Request.Limits");
    descriptor_count=(int)request_u32(); valid(descriptor_count>=0 && descriptor_count<=(int)max_artifacts,"Limit.Artifacts");
    for(int i=0;i<descriptor_count;i++) {
        int id_length=request_text(descriptors[i].id,32); valid(is_id((unsigned char *)descriptors[i].id,id_length),"Request.Id");
        descriptors[i].kind_length=request_text(descriptors[i].kind,1024);
        descriptors[i].name_length=request_text(descriptors[i].name,1024);
    }
    valid(request_at==request_size,"Request.Trailing");
    header_check(path);
    sqlite3 *db=open_configured(uri);
    schema(db); data(db);
    valid(sql_close(db)==0,"Sqlite.Close");
    int64_t summary[8]; memcpy(summary,counts,sizeof(counts)); summary[6]=logical_bytes; summary[7]=instructions;
    frame(5,summary,sizeof(summary));
}
