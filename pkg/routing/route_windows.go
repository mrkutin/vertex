package routing

import "fmt"

// Setup is a stub for Windows — not yet implemented.
func Setup(cfg Config) (*State, error) {
	return nil, fmt.Errorf("routing not implemented on Windows")
}

func cleanup(s *State) error {
	return fmt.Errorf("routing not implemented on Windows")
}
