use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::slice;

const DONE: i32 = -1;
const INVALID: i32 = -2;
const CAPACITY: i32 = -3;
const PANIC: i32 = -4;

struct Node {
    parent: u32,
    action: u32,
    depth: u16,
}

struct Search {
    nodes: Vec<Node>,
    frontier: Vec<u32>,
    current: Option<u32>,
    best: Option<(i64, u32)>,
    limit: u32,
    depth_limit: u16,
    evaluated: u32,
    bounded: bool,
    poisoned: bool,
}

impl Search {
    fn new(limit: u32, depth_limit: u16) -> Option<Self> {
        if !(1..=1_000_000).contains(&limit) || !(1..=64).contains(&depth_limit) {
            return None;
        }
        let mut nodes = Vec::with_capacity(limit as usize);
        nodes.push(Node {
            parent: 0,
            action: 0,
            depth: 0,
        });
        let mut frontier = Vec::with_capacity(limit as usize);
        frontier.push(0);
        Some(Self {
            nodes,
            frontier,
            current: None,
            best: None,
            limit,
            depth_limit,
            evaluated: 0,
            bounded: false,
            poisoned: false,
        })
    }

    fn path(&self, id: u32, output: &mut [u32]) -> i32 {
        let depth = self.nodes[id as usize].depth;
        if output.len() < usize::from(depth) {
            return CAPACITY;
        }
        let mut cursor = id;
        for index in (0..usize::from(depth)).rev() {
            let node = &self.nodes[cursor as usize];
            output[index] = node.action;
            cursor = node.parent;
        }
        i32::from(depth)
    }

    fn next(&mut self, output: &mut [u32]) -> i32 {
        if self.current.is_some() || self.poisoned {
            return INVALID;
        }
        let Some(&id) = self.frontier.last() else {
            return DONE;
        };
        let result = self.path(id, output);
        if result >= 0 {
            self.frontier.pop();
            self.current = Some(id);
        }
        result
    }

    fn observe(&mut self, score: i64, solution: bool, actions: &[u32]) -> i32 {
        if self.poisoned || (solution && !actions.is_empty()) {
            return INVALID;
        }
        let Some(id) = self.current.take() else {
            return INVALID;
        };
        self.evaluated += 1;
        let depth = self.nodes[id as usize].depth;
        if solution
            && self.best.is_none_or(|(best, previous)| {
                score > best || (score == best && depth < self.nodes[previous as usize].depth)
            })
        {
            self.best = Some((score, id));
        }
        let remaining = self.limit as usize - self.nodes.len();
        let count = if depth < self.depth_limit {
            actions.len().min(remaining)
        } else {
            0
        };
        self.bounded |= count < actions.len();
        for &action in actions[..count].iter().rev() {
            let next = u32::try_from(self.nodes.len()).unwrap_or(self.limit);
            self.nodes.push(Node {
                parent: id,
                action,
                depth: depth + 1,
            });
            self.frontier.push(next);
        }
        0
    }

    fn guard(&mut self, operation: impl FnOnce(&mut Self) -> i32) -> i32 {
        if let Ok(result) = catch_unwind(AssertUnwindSafe(|| operation(self))) {
            result
        } else {
            self.poisoned = true;
            PANIC
        }
    }
}

#[derive(Default)]
#[repr(C)]
struct SearchStats {
    allocated: u32,
    evaluated: u32,
    bounded: u32,
    best_found: u32,
    best_score: i64,
}

#[unsafe(no_mangle)]
extern "C" fn cvpp_search_create(limit: u32, depth: u16) -> *mut Search {
    catch_unwind(|| {
        Search::new(limit, depth).map_or(ptr::null_mut(), |search| Box::into_raw(Box::new(search)))
    })
    .unwrap_or(ptr::null_mut())
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_free(search: *mut Search) {
    if !search.is_null() {
        drop(unsafe { Box::from_raw(search) });
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_next(search: *mut Search, output: *mut u32, capacity: u32) -> i32 {
    let Some(search) = (unsafe { search.as_mut() }) else {
        return INVALID;
    };
    if output.is_null() || capacity > 64 {
        return INVALID;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, capacity as usize) };
    search.guard(|search| search.next(output))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_observe(
    search: *mut Search,
    score: i64,
    solution: u32,
    actions: *const u32,
    count: u32,
) -> i32 {
    let Some(search) = (unsafe { search.as_mut() }) else {
        return INVALID;
    };
    if count > 4096 || solution > 1 || (count > 0 && actions.is_null()) {
        return INVALID;
    }
    let actions = if count == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(actions, count as usize) }
    };
    search.guard(|search| search.observe(score, solution == 1, actions))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_best(search: *mut Search, output: *mut u32, capacity: u32) -> i32 {
    let Some(search) = (unsafe { search.as_mut() }) else {
        return INVALID;
    };
    if output.is_null() || capacity > 64 || search.poisoned {
        return INVALID;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, capacity as usize) };
    search.guard(|search| search.best.map_or(DONE, |(_, id)| search.path(id, output)))
}

#[unsafe(no_mangle)]
unsafe extern "C" fn cvpp_search_stats(search: *const Search) -> SearchStats {
    let Some(search) = (unsafe { search.as_ref() }) else {
        return SearchStats::default();
    };
    SearchStats {
        allocated: u32::try_from(search.nodes.len()).unwrap_or(search.limit),
        evaluated: search.evaluated,
        bounded: u32::from(search.bounded),
        best_found: u32::from(search.best.is_some()),
        best_score: search.best.map_or(i64::MIN, |(score, _)| score),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_invalid_limits_and_null_handles() {
        assert!(Search::new(0, 1).is_none());
        assert!(Search::new(1_000_001, 1).is_none());
        assert!(Search::new(10, 0).is_none());
        assert!(Search::new(10, 65).is_none());
        assert_eq!(
            unsafe { cvpp_search_next(ptr::null_mut(), ptr::null_mut(), 0) },
            INVALID
        );
        assert_eq!(
            unsafe { cvpp_search_observe(ptr::null_mut(), 0, 0, ptr::null(), 0) },
            INVALID
        );
    }

    #[test]
    fn contains_panics_and_rejects_later_work() {
        let mut search = Search::new(8, 4).unwrap();
        assert_eq!(search.guard(|_| panic!("test panic")), PANIC);
        assert_eq!(search.next(&mut [0; 4]), INVALID);
        assert_eq!(search.observe(0, true, &[]), INVALID);
    }

    #[test]
    fn explores_dynamic_tree_and_preserves_best_path() {
        let mut search = Search::new(32, 4).unwrap();
        let mut path = [0; 4];
        while let length @ 0.. = search.next(&mut path) {
            let route = &path[..usize::try_from(length).unwrap()];
            let (score, solution, actions): (i64, bool, &[u32]) = match route {
                [] => (0, false, &[10, 20]),
                [10] => (0, false, &[11, 12]),
                [10, 11] => (-5, true, &[]),
                [10, 12] => (12, true, &[]),
                [20] => (7, true, &[]),
                _ => panic!("Unexpected route"),
            };
            assert_eq!(search.observe(score, solution, actions), 0);
        }
        let (score, id) = search.best.unwrap();
        assert_eq!(score, 12);
        assert_eq!(search.path(id, &mut path), 2);
        assert_eq!(&path[..2], &[10, 12]);
        assert_eq!(search.evaluated, 5);
        assert!(!search.bounded);
    }

    #[test]
    fn budget_preserves_completed_solution() {
        let mut search = Search::new(2, 4).unwrap();
        let mut path = [0; 4];
        assert_eq!(search.next(&mut path), 0);
        assert_eq!(search.observe(0, false, &[1, 2, 3]), 0);
        assert_eq!(search.next(&mut path), 1);
        assert_eq!(path[0], 1);
        assert_eq!(search.observe(-10, true, &[]), 0);
        assert_eq!(search.next(&mut path), DONE);
        assert!(search.bounded);
        assert_eq!(search.nodes.len(), 2);
        assert_eq!(search.best.unwrap().0, -10);
    }

    #[test]
    fn invalid_protocol_and_small_buffers_do_not_consume_work() {
        let mut search = Search::new(8, 4).unwrap();
        let mut path = [0; 4];
        assert_eq!(search.observe(0, true, &[]), INVALID);
        assert_eq!(search.next(&mut path), 0);
        assert_eq!(search.next(&mut path), INVALID);
        assert_eq!(search.observe(0, true, &[1]), INVALID);
        assert_eq!(search.observe(0, false, &[42]), 0);
        assert_eq!(search.next(&mut []), CAPACITY);
        assert_eq!(search.next(&mut path), 1);
        assert_eq!(path[0], 42);
    }

    #[test]
    fn depth_limit_and_tie_breaking_are_deterministic() {
        let mut search = Search::new(16, 2).unwrap();
        let mut path = [0; 2];
        assert_eq!(search.next(&mut path), 0);
        search.observe(0, false, &[1, 2, 3]);
        assert_eq!(search.next(&mut path), 1);
        search.observe(0, false, &[4, 5]);
        assert_eq!(search.next(&mut path), 2);
        search.observe(5, true, &[]);
        assert_eq!(search.next(&mut path), 2);
        search.observe(0, false, &[6]);
        assert_eq!(search.next(&mut path), 1);
        search.observe(5, true, &[]);
        assert_eq!(search.next(&mut path), 1);
        search.observe(5, true, &[]);
        assert_eq!(search.next(&mut path), DONE);
        assert!(search.bounded);
        let (_, best) = search.best.unwrap();
        assert_eq!(search.path(best, &mut path), 1);
        assert_eq!(path[0], 2);
    }
}
