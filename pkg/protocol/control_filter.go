package protocol

// ShouldAcceptControl decides whether a control-topic message from
// `topicExit` should be routed to the join-handshake response channel.
//
// During an exit-switch (`switchTarget` != ""), only responses from the new
// target exit are accepted. The old exit's periodic keepalive responses
// keep arriving on its control topic — routing them into the joinResp
// channel would race with the new exit's response, and (since both are
// valid IP-assignment messages) the joinExit() consumer would return
// whichever arrived first. If the old keepalive wins, the switch logically
// "completes" but the TUN keeps the old exit's CIDR/gateway — VPN breaks
// silently and does not self-heal (subsequent keepalive does not call
// reconfigureTUN, only updates the cipher).
//
// In steady state (`switchTarget` == ""), only responses from the current
// session exit are accepted; foreign control traffic is dropped.
//
// Found 2026-05-19 on R3S-inline live test: after [rebalance] ams → sto,
// log read "[switch] done (IP 10.9.3.3)" — but 10.9.3.0/24 is AMS subnet,
// so an AMS keepalive raced and joinExit returned the stale AMS IP instead
// of waiting for STO's response (which arrived later as
// "Duplicate join response for IP 10.9.2.5 (discarded)").
func ShouldAcceptControl(topicExit, sessionExitID, switchTarget string) bool {
	if topicExit == "" {
		return false
	}
	if switchTarget != "" {
		return topicExit == switchTarget
	}
	return topicExit == sessionExitID
}
