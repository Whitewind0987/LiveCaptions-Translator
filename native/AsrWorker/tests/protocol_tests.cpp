#include "protocol.hpp"
#include <fstream>
#include <iostream>
#include <map>
#include <sstream>

using namespace lct;
namespace { int failures=0; void check(bool v,const char*m){if(!v){std::cerr<<m<<'\n';++failures;}} template<class F>void rejects(F f,const char*m){try{f();check(false,m);}catch(const protocol_error&){}} }
int wmain(){
 envelope e{1,0,1,0,3,7,parse_guid(L"00112233-4455-6677-8899-aabbccddeeff")};auto bytes=encode_envelope(e);auto d=decode_envelope(bytes,65536);check(d.sequence==7&&d.correlation==e.correlation,"envelope round-trip");
 check(bytes[24]==0&&bytes[25]==0x11&&bytes[26]==0x22&&bytes[27]==0x33,"RFC Guid order");
 auto bad=bytes;bad[0]=0;rejects([&]{decode_envelope(bad,65536);},"invalid magic rejected");bad=bytes;bad[4]=2;rejects([&]{decode_envelope(bad,65536);},"major rejected");bad=bytes;bad[12]=1;bad[13]=0;bad[14]=1;rejects([&]{decode_envelope(bad,65536);},"oversize rejected");rejects([&]{decode_envelope(std::span<const std::uint8_t>(bytes.data(),20),65536);},"truncated rejected");
 std::vector<std::uint8_t>s;put_string(s,"hello");std::size_t o=0;check(get_string(s,o)=="hello","utf8 string");
 stream_statistics stats{};stats.capture_session=parse_guid(L"11111111-2222-3333-4444-555555555555");audio_metadata m{};m.capture_session=stats.capture_session;m.sequence=1;m.sample_index=0;m.timestamp_ms=1000;m.payload_length=640;check(validate_audio(stats,m),"first audio");m.sequence=3;m.sample_index=320;m.timestamp_ms=1020;check(validate_audio(stats,m)&&stats.gaps==1,"gap accounting");m.sequence=3;check(!validate_audio(stats,m),"duplicate rejected");
 std::ifstream vectors(GOLDEN_VECTOR_PATH);check(vectors.good(),"golden vectors available");std::string line;int count=0;while(std::getline(vectors,line)){if(line.empty()||line[0]=='#')continue;auto split=line.find('=');check(split!=std::string::npos,"vector syntax");if(split!=std::string::npos)++count;}check(count>=10,"shared golden vector count");
 if(failures)return 1;std::cout<<"native protocol tests passed\n";return 0;
}
