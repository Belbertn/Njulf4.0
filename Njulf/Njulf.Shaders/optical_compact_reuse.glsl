// Every lane, including out-of-bounds lanes, must reach both barriers.
// The caller supplies OpticalPublishHistoryReuses(buffer, count).
shared uint OpticalGroupReuses;
void OpticalCountGroupReuse(uint unused) { atomicAdd(OpticalGroupReuses,1u); }
void OpticalBeginHistoryReuses() {
 if(gl_LocalInvocationIndex==0u)OpticalGroupReuses=0u;
 barrier();
}
void OpticalEndHistoryReuses(uint b) {
 barrier();
 if(gl_LocalInvocationIndex==0u && OpticalGroupReuses!=0u)OpticalPublishHistoryReuses(b,OpticalGroupReuses);
}
